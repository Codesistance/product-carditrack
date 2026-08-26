using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Services.PromptContext;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CardiTrack.Infrastructure.Services;

/// <summary>
/// Generates the suggestion shown as "Something to try" on CardiMember Details, and persists it as
/// the member's <see cref="MemberAdvise"/> row — the batch half of "serve Advise from the last batch
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
    /// <c>CARDITRACK_ADVISE_PROMPT</c> — the suggestion shown as "Something to try". Written to
    /// the family about the member, third person throughout: the first generation shipped
    /// "Perhaps try taking a short walk after dinner", which on a caregiver's phone reads as the
    /// app telling the caregiver to walk — the readings are not the reader's. Register is
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
        You are writing to the family about the person being monitored, never to that person:
        every suggestion says what the family could encourage or help the person do, with the
        person named in it in the third person. Never write a bare instruction like an app
        talking to its user — the reader is not the one the readings are about.

        """ + MedicalPromptBlocks.CaregiverRegister + """
        Respond with entries — one per area of wellbeing where the readings genuinely support a
        suggestion, at most one each for Sleep, Activity and HeartRate, and General only for a
        suggestion that spans areas. Include an area only when something in the readings calls for
        it; an empty list is the correct answer when nothing does. Each entry:
        - topic: Sleep, Activity, HeartRate or General, exactly as written.
        - summary: what in the readings this suggestion answers.
        - suggestion: one everyday thing the family could encourage or help this person do,
          grounded in the reference below — never a diagnosis, a prescription, or a change to
          medication or treatment.
        - guidelineCited: which reference below the suggestion draws on, in a few words.

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

        var existing = await _unitOfWork.MemberAdvises.GetAllByCardiMemberAsync(cardiMemberId);
        // The batch writes every topic in one pass, so the newest row's age gates them all.
        if (existing.Count > 0
            && DateTime.UtcNow - existing.Max(a => a.GeneratedAtUtc) < RegenerationInterval)
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

        // One survivor per topic, defensively parsed: an unrecognised topic name is dropped like
        // any other out-of-vocabulary model answer, a second entry for the same topic loses to the
        // first, and the register guards apply per entry exactly as they did to the single row —
        // an entry that names a condition, proposes a treatment, or cites no reference is
        // withheld, not softened.
        var incoming = new Dictionary<AdviseTopic, (string Summary, string Suggestion, string Guideline)>();
        // Topics whose entry came back with a blank summary or suggestion — a transient model
        // hiccup, same stance as the single-row version: the previous suggestion beats none, so
        // the existing row for that topic is kept rather than removed as deliberate silence.
        var hiccups = new HashSet<AdviseTopic>();
        foreach (var entry in aiResponse.Entries)
        {
            if (!Enum.TryParse<AdviseTopic>(entry.Topic, ignoreCase: true, out var topic)
                || !Enum.IsDefined(topic) || incoming.ContainsKey(topic))
                continue;

            var summary = ResolvedOrEmpty(entry.Summary, name);
            var suggestion = ResolvedOrEmpty(entry.Suggestion, name);
            var guideline = ResolvedOrEmpty(entry.GuidelineCited, name);

            if (string.IsNullOrWhiteSpace(summary) || string.IsNullOrWhiteSpace(suggestion))
            {
                hiccups.Add(topic);
                continue;
            }

            if (AdviseRegisterGuards.IsUngroundedCitation(guideline)
                || AdviseRegisterGuards.ReadsAsClinical(summary)
                || AdviseRegisterGuards.ReadsAsClinical(suggestion))
            {
                _logger.LogWarning(
                    "Advise for CardiMember {CardiMemberId} topic {Topic} came back ungrounded or "
                    + "clinical; withholding it.",
                    cardiMemberId, topic);
                continue;
            }

            incoming[topic] = (summary, suggestion, guideline);
        }

        // Reconcile: every topic the model spoke for is upserted; every topic it stayed silent on
        // has its row removed — the prompt makes silence deliberate ("include an area only when
        // something calls for it"), and a suggestion the readings no longer support is worse than
        // none. The whole pass is one SaveChanges, so a reader never sees half a regeneration.
        var removals = existing
            .Where(r => !incoming.ContainsKey(r.Topic) && !hiccups.Contains(r.Topic))
            .ToList();
        foreach (var row in removals)
            _unitOfWork.MemberAdvises.Remove(row);

        // Nothing to write and nothing to remove — a hiccup-only pass, or silence with no rows to
        // withdraw. Saving here would be a no-op flush on every such pass; the single-row version
        // did not pay it and neither does this.
        if (incoming.Count == 0 && removals.Count == 0)
            return;

        var staged = new List<MemberAdvise>();
        foreach (var (topic, (summary, suggestion, guideline)) in incoming)
        {
            var row = existing.FirstOrDefault(r => r.Topic == topic);
            if (row is not null)
            {
                Overwrite(row, summary, suggestion, guideline);
                continue;
            }

            var fresh = new MemberAdvise
            {
                CardiMemberId = cardiMemberId,
                Topic = topic,
                Summary = summary,
                Suggestion = suggestion,
                GuidelineCited = guideline,
                GeneratedAtUtc = DateTime.UtcNow,
            };
            staged.Add(fresh);
            await _unitOfWork.MemberAdvises.AddAsync(fresh);
        }

        try
        {
            await _unitOfWork.SaveChangesAsync();
        }
        catch (DbUpdateException ex)
            when (ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            // Lost an insert race on the unique (CardiMemberId, Topic) index — the same recovery
            // the single-row version applied, per topic: detach this pass's staged inserts, let
            // the winner's rows stand, and overwrite them with this pass's content. Filtered to
            // the unique violation, unlike its predecessor: any other write failure bubbles
            // rather than being "recovered" into a second, harder-to-debug save.
            foreach (var insert in staged)
                _unitOfWork.MemberAdvises.Remove(insert);

            var winners = await _unitOfWork.MemberAdvises.GetAllByCardiMemberAsync(cardiMemberId);
            foreach (var (topic, (summary, suggestion, guideline)) in incoming)
            {
                var winner = winners.FirstOrDefault(r => r.Topic == topic);
                if (winner is not null)
                {
                    Overwrite(winner, summary, suggestion, guideline);
                    continue;
                }

                // No winner for this topic: the race was on a different topic's index, and this
                // one's staged insert was aborted with the rest of the batch. Re-stage it — the
                // first version of this recovery only overwrote winners, which silently dropped
                // every topic the concurrent pass had not written.
                await _unitOfWork.MemberAdvises.AddAsync(new MemberAdvise
                {
                    CardiMemberId = cardiMemberId,
                    Topic = topic,
                    Summary = summary,
                    Suggestion = suggestion,
                    GuidelineCited = guideline,
                    GeneratedAtUtc = DateTime.UtcNow,
                });
            }
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
                  : string.Empty);

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
        /// <summary>At most one entry per <see cref="AdviseTopic"/>; empty when nothing in the
        /// readings supports a suggestion anywhere — which the prompt names as the correct
        /// answer, not a failure. Required so "nothing fits" has to be said as an empty list
        /// rather than an omitted field — the same rationale as the planner's metrics, and what
        /// keeps the schema free of the object-or-null branch the grammar tests forbid.</summary>
        public required IReadOnlyList<AdviseEntryAiResponse> Entries { get; init; }
    }

    internal sealed record AdviseEntryAiResponse
    {
        public required string Topic { get; init; }
        public required string Summary { get; init; }
        public required string Suggestion { get; init; }
        public string? GuidelineCited { get; init; }
    }
}
