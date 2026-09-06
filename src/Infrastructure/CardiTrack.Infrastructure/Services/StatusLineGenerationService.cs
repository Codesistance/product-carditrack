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
/// Generates the Dashboard hero card's status line and persists it as the member's
/// <see cref="MemberStatusLine"/> row — the batch half of "serve the status line from the last
/// batch output" (docs/technical/medgemma_serving_architecture.md, Option B). The digest and
/// assessment passes call this after they change what the line should say; the API only ever
/// reads the row (<see cref="HealthInsightService.GetCurrentStatusMessageAsync"/>).
/// </summary>
/// <remarks>
/// Moved here from <see cref="HealthInsightService"/>, where the same generation ran inside the
/// caregiver's request under a 25 s budget — a shape that forced MedGemma to stay warm for a
/// ~13-call/day surface. In a batch the model is already warm from the pass that triggered the
/// regeneration, and nobody is holding a phone waiting on it.
/// </remarks>
public class StatusLineGenerationService
{
    /// <summary>Same period <see cref="HealthInsightService.PrimaryBaselinePeriodDays"/> keys on.</summary>
    private const int PrimaryBaselinePeriodDays = 30;

    /// <summary>
    /// <c>CARDITRACK_CURRENT_STATUS_PROMPT</c> — a single empathetic line for the Dashboard's
    /// hero card. Ambient, ever-present copy shown on every dashboard view rather than something
    /// a caregiver deliberately opened, so it asks for one short, warm sentence rather than a
    /// structured explanation. The register is <see cref="MedicalPromptBlocks.CaregiverRegister"/>
    /// — no sample phrases, because this model repeats them.
    /// </summary>
    /// <remarks>
    /// Kept short on purpose, and shorter than its siblings. This prompt ran on a caregiver's
    /// request path until the batch move, where every token of instruction was paid in latency on
    /// every call; the discipline stays even though the latency argument has softened — the batch
    /// regenerates on every digest, so prompt length is still inference volume, just billed to
    /// the job instead of the caregiver. <see cref="StatusPromptBudget"/> keeps it that way.
    /// </remarks>
    private const string CurrentStatusInstructions = MedicalPromptBlocks.Tone + """
        Describe how this person is doing to their caregiver.

        Third person, write CardiTrackCardiMember exactly as written; it stands
        in for their real name.
        """ + MedicalPromptBlocks.CaregiverRegister + """
        Match the given tier: green settled, yellow a mention,
        orange or red more attentive.
        Today's steps are a running count, not a day's worth: never call them low or down.

        Respond with:
        - headline: two to five words, sentence case, no full stop, no name
        - message: one sentence under 15 words.

        No preamble, no quotation marks, no explanation.
        """ + MedicalPromptBlocks.ContextGuardrailNotesOnly;

    /// <summary>
    /// Ceiling on <see cref="CurrentStatusInstructions"/>, in characters — the fixed half of the
    /// status prompt, tone block included. Sitting exactly on the measured length is the point:
    /// with no slack, the next addition of any size has to come here and say what it is buying.
    /// (The full history of this constant — the running-count line it was raised for, and the
    /// four-character overshoot it settled at — is in the git history of HealthInsightService,
    /// where it lived until the batch move.)
    /// </summary>
    /// <remarks>
    /// Raised from 1,104 by exactly the 13 characters the placeholder grew when it stopped being
    /// <c>{{NAME}}</c>: one occurrence, and no room bought for anything else. What that spend
    /// buys is in <see cref="NamePlaceholder.Token"/>.
    /// </remarks>
    internal const int StatusPromptBudget = 1_117;

    /// <summary>Exposed for the budget test — the instructions themselves stay private.</summary>
    internal static int CurrentStatusInstructionsLength => CurrentStatusInstructions.Length;

    /// <summary>
    /// Ceiling on the punchy note. Well past the two-to-five words asked for — this is the guard
    /// against a model that answers with a sentence, not the length being aimed at.
    /// </summary>
    private const int MaxStatusHeadlineLength = 40;

    private readonly IUnitOfWork _unitOfWork;
    private readonly IMedicalAiService _medicalAi;
    private readonly MemberContextComposer _memberContext;
    private readonly ILogger<StatusLineGenerationService> _logger;

    public StatusLineGenerationService(
        IUnitOfWork unitOfWork,
        IMedicalAiService medicalAi,
        MemberContextComposer memberContext,
        ILogger<StatusLineGenerationService> logger)
    {
        _unitOfWork = unitOfWork;
        _medicalAi = medicalAi;
        _memberContext = memberContext;
        _logger = logger;
    }

    /// <summary>
    /// Regenerates and persists the member's status line. Callers invoke this after something
    /// that changes what the line should say — a digest write, an alert raised or resolved — and
    /// are expected to treat a failure as theirs to log and swallow: the line is ambient copy,
    /// and a regeneration failure must never sink the digest or assessment that triggered it.
    /// </summary>
    /// <remarks>
    /// A blank model reply leaves the existing row untouched rather than overwriting it: an empty
    /// answer reads as a transient model hiccup, not a stable "nothing to say", and the previous
    /// line (staleness-guarded by the reader) beats no line.
    /// </remarks>
    public async Task RegenerateAsync(Guid cardiMemberId, CancellationToken ct = default)
    {
        var member = await _unitOfWork.CardiMembers.GetByIdAsync(cardiMemberId);
        if (member is null || !member.IsActive || member.IsMonitoringPaused(DateTime.UtcNow))
            return;

        // The unresolved read every other caller makes: IsActive && !IsResolved, done in SQL and
        // untracked. Resolution ends the episode without deactivating the row, so the second
        // filter is the one that matters — and doing it here in memory is how the claim of being
        // "the same read as DashboardService's" quietly stopped being true.
        var unresolvedAlerts = await _unitOfWork.Alerts.GetUnresolvedByCardiMemberAsync(cardiMemberId);

        // The member's own civil day, not the host's — the same anchor the digest resolves.
        var timeZone = await MemberAnchorTimeZone.ResolveAsync(_unitOfWork, cardiMemberId);
        var utcNow = DateTime.UtcNow;
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(utcNow, timeZone);
        var today = DateOnly.FromDateTime(localNow);

        var highestAlert = unresolvedAlerts.Count == 0
            ? AlertSeverity.Green
            : unresolvedAlerts.Max(a => a.Severity);
        var latestAssessment = await _unitOfWork.RealtimeAssessments.GetLatestAsync(cardiMemberId, ct);
        var latestDigest = await _unitOfWork.Digests.GetLatestByDateAsync(
            cardiMemberId, today, DigestAudience.Family, ct);
        var severity = StatusDisplayTier.Resolve(
                highestAlert, latestAssessment, latestDigest, utcNow)
            .ToString().ToLowerInvariant();

        var recentLogs = await _unitOfWork.ActivityLogs
            .GetByCardiMemberAndDateRangeAsync(cardiMemberId, today.AddDays(-2), today);

        var baseline = await _unitOfWork.PatternBaselines
            .GetLatestByCardiMemberAsync(cardiMemberId, PrimaryBaselinePeriodDays);
        var progress = DigestDayProgress.For(localNow, baseline, timeZone);

        var memberContext = await _memberContext.ComposeAsync(
            new MemberContextRequest(member, cardiMemberId, today, DateTime.UtcNow, PromptPurpose.CurrentStatus), ct);

        var prompt = BuildCurrentStatusPrompt(
            memberContext, severity, unresolvedAlerts, recentLogs, today, progress);
        var aiResponse = await _medicalAi.GenerateStructuredAsync<CurrentStatusAiResponse>(prompt, ct);

        // Resolved before persisting: the row is what every dashboard view reads until the next
        // regeneration, so an unresolved placeholder would show braces to a caregiver for that
        // whole window. An unresolvable message leaves the previous line standing.
        var name = NamePlaceholder.FirstName(member.Name);
        var message = NamePlaceholder.Resolve(aiResponse.Message.Trim(), name) ?? string.Empty;
        if (NamePlaceholder.IsPresentIn(message))
            message = string.Empty;
        // The headline is not resolved — it is asked not to name them, and the card already
        // shows who this is — so a leftover placeholder is dropped rather than turned into a
        // name in the title. A missing headline does not sink the sentence.
        var headline = CleanStatusHeadline(aiResponse.Headline);
        if (NamePlaceholder.IsPresentIn(headline))
            headline = null;

        if (string.IsNullOrWhiteSpace(message))
        {
            _logger.LogWarning(
                "Status line for CardiMember {CardiMemberId} came back blank; keeping the previous line.",
                cardiMemberId);
            return;
        }

        var existing = await _unitOfWork.MemberStatusLines.GetByCardiMemberAsync(cardiMemberId);
        if (existing is not null)
        {
            Overwrite(existing, headline, message);
            // The generic repository stages rather than executes — without this the row would be
            // dropped when the scope ends (same note as the questionnaire write in the digest).
            await _unitOfWork.SaveChangesAsync();
            return;
        }

        var fresh = new MemberStatusLine
        {
            CardiMemberId = cardiMemberId,
            Headline = headline,
            Message = message,
            GeneratedAtUtc = DateTime.UtcNow,
        };
        await _unitOfWork.MemberStatusLines.AddAsync(fresh);
        try
        {
            await _unitOfWork.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // Lost the insert race on the unique CardiMemberId index: the digest pass and the
            // assessor can regenerate the same member concurrently, and both read null above.
            // Detach our staged insert (Remove on an Added entity detaches, it deletes nothing)
            // and write over the winner's row instead — last writer wins, exactly as the update
            // path behaves when the read had found the row.
            _unitOfWork.MemberStatusLines.Remove(fresh);
            var winner = await _unitOfWork.MemberStatusLines.GetByCardiMemberAsync(cardiMemberId)
                ?? throw new InvalidOperationException(
                    $"Insert of the status line for CardiMember {cardiMemberId} failed, but no "
                    + "existing row was found — not the unique-index race this handles.");
            Overwrite(winner, headline, message);
            await _unitOfWork.SaveChangesAsync();
        }
    }

    private static void Overwrite(MemberStatusLine line, string? headline, string message)
    {
        line.Headline = headline;
        line.Message = message;
        line.GeneratedAtUtc = DateTime.UtcNow;
        line.UpdatedDate = DateTime.UtcNow;
    }

    /// <summary>
    /// The headline is a label, not prose: a trailing full stop or a wrapping quote reads wrong
    /// as a card title, and an answer that ran on into a sentence is not a headline at all. One
    /// that fails is dropped rather than fixed up — the dashboard keeps the per-tier headline it
    /// already rendered, which is a better line than a mangled one.
    /// </summary>
    private static string? CleanStatusHeadline(string? headline)
    {
        var cleaned = (headline ?? string.Empty).Trim().Trim('"', '\'', '.', '—', '-').Trim();
        return cleaned.Length is 0 or > MaxStatusHeadlineLength ? null : cleaned;
    }

    private static string BuildCurrentStatusPrompt(
        string memberContext,
        string severity,
        IReadOnlyCollection<Alert> unresolvedAlerts,
        IEnumerable<ActivityLog> recentLogs,
        DateOnly today,
        DigestDayProgress progress)
    {
        // Titles only. The type and severity of each alert feed the tier above, alongside a
        // recent yellow-or-worse assessment and today's family digest — see
        // <see cref="StatusDisplayTier"/>. Flattened, as every other renderer that carries an
        // alert title does: this one puts each on its own "- " line inside a section, so a
        // newline in a title would open a line the section never labelled.
        var alertContext = unresolvedAlerts.Count == 0
            ? "No unresolved alerts."
            : string.Join("\n", unresolvedAlerts.Select(a => $"- {MedicalPromptBlocks.Flatten(a.Title)}"));

        return $"""
            {CurrentStatusInstructions}

            {memberContext}

            --- Current severity tier ---
            {severity}

            --- Unresolved alerts ---
            {alertContext}

            --- Recent readings (the most recent days that carried any, oldest first) ---
            {StatusActivityLines(recentLogs, today, progress)}
            """;
    }

    /// <summary>
    /// The same daily readings <see cref="MedicalPromptBlocks.DailyLines"/> renders, said in about
    /// a third of the tokens — a different question, not a simplification: this prompt is asked
    /// for a single sentence about today, where the shape of the last three days is context
    /// rather than something to be quoted back.
    /// </summary>
    /// <param name="progress">
    /// How far into their day the member is. The one place this prompt spends extra words on a
    /// label, because "(partial)" alone is what let a hero line read "Steps are lower today" at
    /// 07:14 — the model comparing a just-woken member's running total against yesterday's
    /// finished one, the only two rows it had. See <see cref="DigestDayProgress"/>.
    /// </param>
    private static string StatusActivityLines(
        IEnumerable<ActivityLog> logs, DateOnly today, DigestDayProgress progress)
    {
        var lines = logs
            .TakeLast(3)
            .Select(l =>
            {
                var label = (today.DayNumber - l.Date.DayNumber) switch
                {
                    <= 0 => $"Today so far ({progress.Describe()})",
                    1 => "Yesterday",
                    var days => $"{days} days ago",
                };

                // Only what the device reported. Interpolating the nullable straight into the line
                // rendered "steps=, HR=, sleep(night ending that morning)=min" for a member whose
                // watch missed a metric — an empty value beside a real one, on the prompt asked for
                // a single reassuring sentence. The digest's own renderer guards this and its tests
                // assert on it (DoesNotContain "steps=,"); this line never did.
                var figures = new List<string>(3);
                if (l.Steps is { } steps)
                    figures.Add($"steps={steps}");
                if (l.RestingHeartRate is { } resting)
                    figures.Add($"HR={resting}");
                if (l.SleepMinutes is { } sleep)
                    figures.Add($"sleep(night ending that morning)={sleep}min");

                return $"  {label}: {(figures.Count > 0 ? string.Join(", ", figures) : "nothing measured")}";
            })
            .ToList();

        return lines.Count > 0 ? string.Join("\n", lines) : "No recent activity data.";
    }

    // Internal rather than private so IMedicalAiService.GenerateStructuredAsync<T> can be
    // exercised directly in tests.
    internal sealed record CurrentStatusAiResponse
    {
        public string? Headline { get; init; }
        public required string Message { get; init; }
    }
}
