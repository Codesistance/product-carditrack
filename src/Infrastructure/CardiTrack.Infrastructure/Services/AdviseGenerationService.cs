using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Infrastructure.Services.PromptContext;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CardiTrack.Infrastructure.Services;

/// <summary>
/// Generates the wellness suggestion shown as a Tip on CardiMember Details, and persists it as the
/// member's <see cref="MemberAdvise"/> row — the batch half of "serve Advise from the last batch
/// output", the same shape as <see cref="StatusLineGenerationService"/>. The digest pass calls this
/// after it changes what there is to say; the API only ever reads the row
/// (<see cref="HealthInsightService.GetAdviseAsync"/>), and the Dashboard card's pulse indicator
/// reads only whether a row exists — neither ever calls the model.
/// </summary>
/// <remarks>
/// Gated on its own regeneration interval rather than running on every trigger, unlike
/// <see cref="StatusLineGenerationService"/>: a status line is ambient copy read on every dashboard
/// view and worth refreshing on every digest/assess pass, but a wellness suggestion does not need
/// to move that often, and MedGemma's dev cost profile makes cadence the one lever that matters —
/// running this on the same half-hourly-plus-5-minute cadence as the status line would multiply
/// call volume across the whole member base for no benefit a caregiver would notice.
/// </remarks>
public class AdviseGenerationService
{
    /// <summary>Same period <see cref="HealthInsightService.PrimaryBaselinePeriodDays"/> keys on.</summary>
    private const int PrimaryBaselinePeriodDays = 30;

    /// <summary>
    /// How often this regenerates, at most. Checked against the existing row's
    /// <see cref="MemberAdvise.GeneratedAtUtc"/> before spending a model call — the due-check this
    /// service's whole cost discipline rests on.
    /// </summary>
    private static readonly TimeSpan RegenerationInterval = TimeSpan.FromDays(1);

    /// <summary>
    /// <c>CARDITRACK_ADVISE_PROMPT</c> — the wellness suggestion shown as a Tip. Register is
    /// <see cref="MedicalPromptBlocks.CaregiverRegister"/>, opened by
    /// <see cref="MedicalPromptBlocks.ToneWellness"/> rather than <see cref="MedicalPromptBlocks.Tone"/>:
    /// this is the one generation on this platform whose whole job is to suggest something, so the
    /// ordinary diagnosis ban is not enough — it also has to say what kind of suggestion this is
    /// allowed to be. Grounded in <see cref="MedicalPromptBlocks.WellnessGuidelineReference"/> rather
    /// than the model's own unconstrained medical reasoning, and asks the model to name which
    /// reference it drew from so an ungrounded reply is one the code can recognise and withhold.
    /// </summary>
    private const string AdviseInstructions =
        MedicalPromptBlocks.ToneWellness + MedicalPromptBlocks.Pronouns + """
        Suggest one everyday thing the family could try for this person, grounded in the
        reference below and their own recent readings and baseline.

        """ + MedicalPromptBlocks.CaregiverRegister + """
        Respond with:
        - summary: what in the readings prompted this suggestion.
        - suggestion: one everyday thing the family could try, grounded in the reference below —
          never a diagnosis, a prescription, or a change to medication or treatment.
        - guidelineCited: which reference below the suggestion draws on, in a few words. If nothing
          in the reference fits the readings, say there is nothing to suggest right now and leave
          this blank.

        """ + MedicalPromptBlocks.ContextGuardrail;

    private readonly IUnitOfWork _unitOfWork;
    private readonly IMedicalAiService _medicalAi;
    private readonly MemberContextComposer _memberContext;
    private readonly ILogger<AdviseGenerationService> _logger;

    public AdviseGenerationService(
        IUnitOfWork unitOfWork,
        IMedicalAiService medicalAi,
        MemberContextComposer memberContext,
        ILogger<AdviseGenerationService> logger)
    {
        _unitOfWork = unitOfWork;
        _medicalAi = medicalAi;
        _memberContext = memberContext;
        _logger = logger;
    }

    /// <summary>
    /// Regenerates and persists the member's wellness suggestion, but only when the existing row
    /// (if any) is past <see cref="RegenerationInterval"/> — callers are expected to invoke this on
    /// every digest pass and rely on the due-check rather than gating the call themselves. Treats a
    /// failure as theirs to log and swallow: Advise is a suggestion, not the digest or assessment
    /// that triggered this call.
    /// </summary>
    /// <remarks>
    /// Two different empty replies are told apart. A blank <c>summary</c> or <c>suggestion</c>
    /// reads as a transient model hiccup — the same stance <see cref="StatusLineGenerationService"/>
    /// takes — and leaves the existing row untouched. A blank <c>guidelineCited</c> beside a
    /// well-formed summary and suggestion is different: the prompt explicitly asks the model to say
    /// so when nothing in the wellness reference fits the readings, so that is intentional, not a
    /// hiccup, and the honest response is to withhold the row entirely — a stale suggestion that no
    /// longer applies is worse than none, especially for a feature this careful about not reading
    /// as a clinical instruction.
    /// <para>
    /// A well-formed reply that names a condition or proposes a treatment is withheld the same way,
    /// and so is a citation that names no reference. Both are <see cref="AdviseRegisterGuards"/>'s
    /// job: the prompt states the boundary and cannot enforce it, and a nonblank check on the
    /// citation is not the traceability check the field was added for — "N/A" passes it. This is
    /// the guard every comparable generation on the platform already had and this one did not.
    /// </para>
    /// </remarks>
    public async Task RegenerateIfDueAsync(Guid cardiMemberId, CancellationToken ct = default)
    {
        var member = await _unitOfWork.CardiMembers.GetByIdAsync(cardiMemberId);
        if (member is null || !member.IsActive || member.IsMonitoringPaused(DateTime.UtcNow))
            return;

        var existing = await _unitOfWork.MemberAdvises.GetByCardiMemberAsync(cardiMemberId);
        if (existing is not null && DateTime.UtcNow - existing.GeneratedAtUtc < RegenerationInterval)
            return;

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var recentLogs = await _unitOfWork.ActivityLogs
            .GetByCardiMemberAndDateRangeAsync(cardiMemberId, today.AddDays(-14), today);

        var baseline = await _unitOfWork.PatternBaselines
            .GetLatestByCardiMemberAsync(cardiMemberId, PrimaryBaselinePeriodDays);

        var memberContext = await _memberContext.ComposeAsync(
            new MemberContextRequest(member, cardiMemberId, today, DateTime.UtcNow, PromptPurpose.Advise), ct);

        var prompt = BuildAdvisePrompt(memberContext, baseline, recentLogs, today);
        var aiResponse = await _medicalAi.GenerateStructuredAsync<AdviseAiResponse>(prompt, ct);

        var name = NamePlaceholder.FirstName(member.Name);
        var summary = ResolvedOrEmpty(aiResponse.Summary, name);
        var suggestion = ResolvedOrEmpty(aiResponse.Suggestion, name);
        var guidelineCited = ResolvedOrEmpty(aiResponse.GuidelineCited, name);

        // Two different "nothing to serve" replies, withheld the same way. A citation naming no
        // reference is the model declining to ground the suggestion — which the prompt explicitly
        // invites when nothing fits, and which AdviseRegisterGuards also recognises in the
        // placeholders a model reaches for instead of leaving the field blank. A summary or
        // suggestion that names a condition or proposes a treatment is the model crossing the one
        // boundary this generation exists inside; the prompt asks it not to, and asking is not
        // enforcing (see AdviseRegisterGuards). Either way the honest outcome is no row: a
        // suggestion is not so valuable that it is worth serving one that broke its own contract.
        if (AdviseRegisterGuards.IsUngroundedCitation(guidelineCited)
            || AdviseRegisterGuards.ReadsAsClinical(summary)
            || AdviseRegisterGuards.ReadsAsClinical(suggestion))
        {
            if (AdviseRegisterGuards.ReadsAsClinical(summary) || AdviseRegisterGuards.ReadsAsClinical(suggestion))
            {
                _logger.LogWarning(
                    "Advise for CardiMember {CardiMemberId} came back naming a condition or a "
                    + "treatment; withholding it.",
                    cardiMemberId);
            }

            if (existing is not null)
            {
                _unitOfWork.MemberAdvises.Remove(existing);
                await _unitOfWork.SaveChangesAsync();
            }
            return;
        }

        if (string.IsNullOrWhiteSpace(summary) || string.IsNullOrWhiteSpace(suggestion))
        {
            _logger.LogWarning(
                "Advise for CardiMember {CardiMemberId} came back blank; keeping the previous suggestion.",
                cardiMemberId);
            return;
        }

        if (existing is not null)
        {
            Overwrite(existing, summary, suggestion, guidelineCited);
            await _unitOfWork.SaveChangesAsync();
            return;
        }

        var fresh = new MemberAdvise
        {
            CardiMemberId = cardiMemberId,
            Summary = summary,
            Suggestion = suggestion,
            GuidelineCited = guidelineCited,
            GeneratedAtUtc = DateTime.UtcNow,
        };
        await _unitOfWork.MemberAdvises.AddAsync(fresh);
        try
        {
            await _unitOfWork.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // Lost the insert race on the unique CardiMemberId index — same recovery
            // StatusLineGenerationService applies: detach the staged insert and overwrite the
            // winner's row instead.
            _unitOfWork.MemberAdvises.Remove(fresh);
            var winner = await _unitOfWork.MemberAdvises.GetByCardiMemberAsync(cardiMemberId)
                ?? throw new InvalidOperationException(
                    $"Insert of the Advise row for CardiMember {cardiMemberId} failed, but no "
                    + "existing row was found — not the unique-index race this handles.");
            Overwrite(winner, summary, suggestion, guidelineCited);
            await _unitOfWork.SaveChangesAsync();
        }
    }

    private static void Overwrite(
        MemberAdvise advise, string summary, string suggestion, string guidelineCited)
    {
        advise.Summary = summary;
        advise.Suggestion = suggestion;
        advise.GuidelineCited = guidelineCited;
        advise.GeneratedAtUtc = DateTime.UtcNow;
        advise.UpdatedDate = DateTime.UtcNow;
    }

    /// <summary>
    /// Substitutes <see cref="NamePlaceholder.Token"/> when a name is on file, and drops leftover
    /// braces rather than returning them — the same guard <c>HealthInsightService.ResolvedOrEmpty</c>
    /// applies to its own AI replies.
    /// </summary>
    private static string ResolvedOrEmpty(string? text, string? name)
    {
        var resolved = NamePlaceholder.Resolve(text, name) ?? string.Empty;
        return NamePlaceholder.IsPresentIn(resolved) ? string.Empty : resolved;
    }

    private static string BuildAdvisePrompt(
        string memberContext,
        PatternBaseline? baseline,
        IEnumerable<ActivityLog> recentLogs,
        DateOnly today)
    {
        var baselineInfo = baseline is null
            ? "No baseline established yet — this member is still being learned."
            : $"{baseline.PeriodDays}-day — Steps: {baseline.AvgSteps}±{baseline.StdDevSteps}, " +
              $"Resting HR: {baseline.AvgRestingHeartRate}±{baseline.StdDevHeartRate}, " +
              $"Sleep: {baseline.AvgSleepMinutes} min" +
              // Only where the member's device reports it: an "HRV: ± " with nothing either side
              // is a yardstick the model would try to use.
              (baseline.AvgHeartRateVariabilityMs is { } hrv
                  ? $", HRV: {hrv}±{baseline.StdDevHeartRateVariability} ms overnight"
                  : string.Empty) +
              (baseline.AvgWeightKg is { } weight ? $", Weight: {weight} kg" : string.Empty);

        return $"""
            {AdviseInstructions}

            {memberContext}

            --- General health reference ---
            {MedicalPromptBlocks.WellnessGuidelineReference}

            --- Baseline ---
            {baselineInfo}

            --- Recent readings (the most recent days that carried any, oldest first) ---
            {MedicalPromptBlocks.DailyLines(recentLogs, take: 7, today)}
            """;
    }

    // Internal rather than private so IMedicalAiService.GenerateStructuredAsync<T> can be
    // exercised directly in tests.
    internal sealed record AdviseAiResponse
    {
        public required string Summary { get; init; }
        public required string Suggestion { get; init; }
        public string? GuidelineCited { get; init; }
    }
}
