using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Infrastructure.Services.PromptContext;

namespace CardiTrack.Infrastructure.Services;

public class HealthInsightService : IHealthInsightService
{
    /// <summary>
    /// The 30-day baseline is the one <c>DashboardService</c> uses to decide whether a member is still
    /// being learned, so the same period decides which prompt this service sends.
    /// </summary>
    private const int PrimaryBaselinePeriodDays = 30;

    /// <summary>Baseline windows compared in a trend analysis, shortest first.</summary>
    private static readonly int[] BaselinePeriodDays = [PrimaryBaselinePeriodDays, 60, 90];

    /// <summary>
    /// Provisional windows tried, longest first, when no 30-day baseline exists yet. These get the
    /// tentative prompt below rather than the trend prompt — an early impression is not a trend.
    /// </summary>
    private static readonly int[] ProvisionalPeriodDays = [14, 7];

    // ── Fixed instruction blocks ────────────────────────────────────────────────
    // These lead every prompt and must stay byte-identical between calls: the serving engine can
    // only reuse a cached prefix that has not changed, and personalising them would throw that away
    // for every member (docs/llm_design.md). Member data always goes *after* them.

    /// <summary>
    /// <c>CARDITRACK_ALERT_PROMPT</c> — explains a fired alert to a caregiver. The register is
    /// <see cref="MedicalPromptBlocks.CaregiverRegister"/>: everyday words, a lay mention so the
    /// family can be informed and react, not clinic-speak and not a fix. "Flag for review" was
    /// the old clinical-queue brief and does not belong on a line a family reads. The action is
    /// one specific thing they can do now — named by the model from this alert, not chosen from
    /// a list of examples it would otherwise repeat for every member.
    /// </summary>
    private const string AlertInstructions =
        MedicalPromptBlocks.Tone + MedicalPromptBlocks.Pronouns + """
        Explain this alert to a family caregiver.

        """ + MedicalPromptBlocks.CaregiverRegister + """
        Write CardiTrackCardiMember exactly as written wherever you would name the person; it stands in
        for their real name, which you are not given.

        Respond with:
        - explanation: what this alert means in the recent readings, and a lay mention of what
          may sit behind it if the readings support one.
        - recommendedAction: one specific thing the caregiver can do now that answers this
          alert. Never start, stop or change medication, never a diagnosis, and never a fix.

        Keep both fields factual and concise.
        """ + MedicalPromptBlocks.ContextGuardrail;

    /// <summary>
    /// <c>CARDITRACK_BASELINE_PROMPT</c> — trend analysis once a 30-day baseline exists. The
    /// register is <see cref="MedicalPromptBlocks.CaregiverRegister"/>. "Flag for review" was the
    /// old clinical-queue brief and does not belong on a line a family reads.
    /// </summary>
    private const string BaselineInstructions =
        MedicalPromptBlocks.Tone + MedicalPromptBlocks.Pronouns + """
        Describe this person's health trends against the established baseline.

        """ + MedicalPromptBlocks.CaregiverRegister + """
        Respond with:
        - summary: this person's overall health trends, including any patterns that warrant
          caregiver attention.
        - keyFindings: short strings, one per key finding.
        """ + MedicalPromptBlocks.ContextGuardrail;

    /// <summary>
    /// <c>CARDITRACK_LEARNING_PROMPT</c> — the first weeks, before a baseline exists. Nothing can be
    /// called unusual yet because there is no normal to compare against, so this asks for a picture
    /// of what has been observed rather than a judgement. The register is
    /// <see cref="MedicalPromptBlocks.CaregiverRegister"/>. The words it must not use are not
    /// listed: MedGemma would echo them.
    /// </summary>
    private const string LearningInstructions =
        MedicalPromptBlocks.Tone + MedicalPromptBlocks.Pronouns + """
        Describe what the readings have shown so far.
        There is not yet enough history to know this person's normal, so call nothing unusual.

        """ + MedicalPromptBlocks.CaregiverRegister + """
        Respond with:
        - summary: the daily rhythm shown so far, and what is still needed for a reliable
          picture of this member.
        - keyFindings: short strings, one per key observation.
        """ + MedicalPromptBlocks.ContextGuardrail;

    /// <summary>
    /// <c>CARDITRACK_PROVISIONAL_PROMPT</c> — a provisional (sub-30-day) baseline exists. There is
    /// an early picture to compare against, but not an established normal, so the framing sits
    /// between the learning prompt (no comparisons at all) and the trend prompt (confident
    /// comparisons): comparisons are impressions, and a short window is not settled. The register
    /// is <see cref="MedicalPromptBlocks.CaregiverRegister"/>. Sample hedges are not listed:
    /// MedGemma would echo them.
    /// </summary>
    private const string ProvisionalInstructions =
        MedicalPromptBlocks.Tone + MedicalPromptBlocks.Pronouns + """
        Describe an early reading against this short window.
        The baseline is provisional — under 30 days of history — so a comparison is an impression, not an established pattern.
        Do not treat so short a window as settled.

        """ + MedicalPromptBlocks.CaregiverRegister + """
        Respond with:
        - summary: what the early data suggests, and what will become clearer once the full
          30-day baseline is established.
        - keyFindings: short strings, one per key observation.
        """ + MedicalPromptBlocks.ContextGuardrail;

    // The current-status prompt, its budget and the generation path moved to
    // StatusLineGenerationService with the batch move: the line is generated by the pipeline's
    // digest and assess passes and persisted per member, and this service only reads the row.

    /// <summary>
    /// A batch-generated line older than this is withheld rather than served: the digest pass
    /// regenerates on every meaningful data change, so a day-old row means generation has stopped
    /// for this member, and yesterday's reassurance presented as current would say something
    /// false. The dashboard's per-tier fallback copy is the honest answer then.
    /// </summary>
    private static readonly TimeSpan StatusLineMaxAge = TimeSpan.FromHours(24);

    private readonly IMedicalAiService _medicalAi;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICardiMemberAccessService _access;
    private readonly MemberContextComposer _memberContext;

    public HealthInsightService(
        IMedicalAiService medicalAi,
        IUnitOfWork unitOfWork,
        ICardiMemberAccessService access,
        MemberContextComposer memberContext)
    {
        _medicalAi = medicalAi;
        _unitOfWork = unitOfWork;
        _access = access;
        _memberContext = memberContext;
    }

    /// <summary>
    /// The member-context sections for one of this service's four prompts. A thin wrapper so each
    /// caller states only which prompt it is building — the sources decide what belongs in it.
    /// </summary>
    private Task<string> ComposeMemberContextAsync(
        CardiMember? member, Guid cardiMemberId, DateOnly today, PromptPurpose purpose, CancellationToken ct) =>
        _memberContext.ComposeAsync(
            new MemberContextRequest(member, cardiMemberId, today, DateTime.UtcNow, purpose), ct);

    public async Task<AlertInsightResponse> AnalyzeAlertAsync(
        Guid requestingUserId, Guid alertId, CancellationToken ct = default)
    {
        // An alert is reachable only through its CardiMember. Both "no such alert" and
        // "not your alert" report the same not-found so the alert id cannot be probed.
        var alert = await _unitOfWork.Alerts.GetByIdWithCardiMemberAsync(alertId);
        if (alert is null || !await _access.HasViewAccessAsync(requestingUserId, alert.CardiMemberId, ct))
            throw new KeyNotFoundException($"Alert {alertId} not found.");

        var to = DateOnly.FromDateTime(DateTime.UtcNow);
        var from = to.AddDays(-7);
        var recentLogs = await _unitOfWork.ActivityLogs
            .GetByCardiMemberAndDateRangeAsync(alert.CardiMemberId, from, to);

        var member = await _unitOfWork.CardiMembers.GetByIdAsync(alert.CardiMemberId);

        var baseline = await _unitOfWork.PatternBaselines
            .GetLatestByCardiMemberAsync(alert.CardiMemberId, PrimaryBaselinePeriodDays);

        var memberContext = await ComposeMemberContextAsync(
            member, alert.CardiMemberId, to, PromptPurpose.AlertInsight, ct);

        var prompt = BuildAlertPrompt(alert, memberContext, recentLogs, baseline, to);
        var aiResponse = await _medicalAi.GenerateStructuredAsync<AlertAiResponse>(prompt, ct);

        var name = NamePlaceholder.FirstName(member?.Name);
        return new AlertInsightResponse
        {
            AlertId = alertId,
            Explanation = ResolvedOrEmpty(aiResponse.Explanation, name),
            Severity = alert.Severity,
            RecommendedAction = ResolvedOrEmpty(aiResponse.RecommendedAction, name),
        };
    }

    /// <summary>
    /// Substitutes <see cref="NamePlaceholder.Token"/> when a name is on file. Leftover braces
    /// are dropped rather than returned: the status line and the digest already refuse to show
    /// them, and an insight that still says <c>CardiTrackCardiMember</c> is worse than an empty field.
    /// </summary>
    private static string ResolvedOrEmpty(string? text, string? name)
    {
        var resolved = NamePlaceholder.Resolve(text, name) ?? string.Empty;
        return NamePlaceholder.IsPresentIn(resolved) ? string.Empty : resolved;
    }

    public async Task<BaselineInsightResponse> AnalyzeBaselineAsync(
        Guid requestingUserId, Guid cardiMemberId, CancellationToken ct = default)
    {
        await _access.RequireViewAccessAsync(requestingUserId, cardiMemberId, ct);

        // Sequential, not Task.WhenAll. These lookups all run against the request's DbContext,
        // and EF Core refuses a second operation on a context while one is still running —
        // starting them together threw before the first result came back, so this endpoint
        // failed on every call. Three indexed point-lookups cost little in series.
        // The 30-day baseline is tracked by name rather than by position: the list holds only the
        // windows that exist, so an index would point at the 60-day baseline whenever the 30-day
        // one is missing — exactly the case that decides which prompt gets sent.
        var baselines = new List<Domain.Entities.PatternBaseline>();
        Domain.Entities.PatternBaseline? primaryBaseline = null;

        foreach (var periodDays in BaselinePeriodDays)
        {
            var baseline = await _unitOfWork.PatternBaselines
                .GetLatestByCardiMemberAsync(cardiMemberId, periodDays);
            if (baseline is null)
                continue;

            if (periodDays == PrimaryBaselinePeriodDays)
                primaryBaseline = baseline;

            baselines.Add(baseline);
        }

        var to = DateOnly.FromDateTime(DateTime.UtcNow);
        var from = to.AddDays(-14);
        var recentLogs = (await _unitOfWork.ActivityLogs
            .GetByCardiMemberAndDateRangeAsync(cardiMemberId, from, to)).ToList();

        var member = await _unitOfWork.CardiMembers.GetByIdAsync(cardiMemberId);

        // No 30-day baseline yet: fall back to the best provisional window before declaring the
        // member still-learning — the same preference order DashboardService applies, so the
        // app's "getting to know you" state and this summary never disagree.
        Domain.Entities.PatternBaseline? provisionalBaseline = null;
        if (primaryBaseline is null)
        {
            foreach (var periodDays in ProvisionalPeriodDays)
            {
                provisionalBaseline = await _unitOfWork.PatternBaselines
                    .GetLatestByCardiMemberAsync(cardiMemberId, periodDays);
                if (provisionalBaseline is not null)
                    break;
            }
        }

        var isLearning = primaryBaseline is null && provisionalBaseline is null;

        var memberContext = await ComposeMemberContextAsync(
            member, cardiMemberId, to, PromptPurpose.BaselineInsight, ct);

        var prompt = (primaryBaseline, provisionalBaseline) switch
        {
            (not null, _) => BuildBaselinePrompt(memberContext, baselines, recentLogs, to),
            (null, not null) => BuildProvisionalPrompt(memberContext, provisionalBaseline, recentLogs, to),
            _ => BuildLearningPrompt(memberContext, recentLogs, to),
        };

        var aiResponse = await _medicalAi.GenerateStructuredAsync<BaselineAiResponse>(prompt, ct);

        return new BaselineInsightResponse
        {
            CardiMemberId = cardiMemberId,
            Summary = aiResponse.Summary,
            KeyFindings = aiResponse.KeyFindings,
            IsLearning = isLearning,
            IsProvisional = provisionalBaseline is not null,
            BaselinePeriodDays = (primaryBaseline ?? provisionalBaseline)?.PeriodDays,
            GeneratedAt = DateTimeOffset.UtcNow
        };
    }

    /// <summary>"Nothing to say yet" — the contract's own way of saying it, so every path that
    /// declines to answer does so in the shape the dashboard already handles.</summary>
    private static CurrentStatusMessageResponse NoStatusMessage() =>
        new() { Headline = null, Message = null, GeneratedAt = DateTimeOffset.UtcNow };

    /// <summary>
    /// Read-only since the batch move: the line is generated by the pipeline's digest and assess
    /// passes (<see cref="StatusLineGenerationService"/>) and persisted per member; this serves
    /// the latest row. No model call, no cache, no budget — a dashboard load costs one indexed
    /// lookup, which is what lets MedGemma scale to zero between batch windows
    /// (docs/technical/medgemma_serving_architecture.md, Option B).
    /// </summary>
    public async Task<CurrentStatusMessageResponse> GetCurrentStatusMessageAsync(
        Guid requestingUserId, Guid cardiMemberId, CancellationToken ct = default)
    {
        await _access.RequireViewAccessAsync(requestingUserId, cardiMemberId, ct);

        // The same guard the background generators apply before spending a model call — kept on
        // the read too: a paused or deactivated member's stored line describes a monitoring state
        // that no longer exists, and serving it would say something false.
        var member = await _unitOfWork.CardiMembers.GetByIdAsync(cardiMemberId);
        if (member is null || !member.IsActive || member.IsMonitoringPaused(DateTime.UtcNow))
            return NoStatusMessage();

        var line = await _unitOfWork.MemberStatusLines.GetByCardiMemberAsync(cardiMemberId);
        if (line is null || DateTime.UtcNow - line.GeneratedAtUtc > StatusLineMaxAge)
            return NoStatusMessage();

        return new CurrentStatusMessageResponse
        {
            Headline = line.Headline,
            Message = line.Message,
            GeneratedAt = new DateTimeOffset(DateTime.SpecifyKind(line.GeneratedAtUtc, DateTimeKind.Utc)),
        };
    }

    private static string BuildAlertPrompt(
        Alert alert,
        string memberContext,
        IEnumerable<ActivityLog> recentLogs,
        PatternBaseline? baseline,
        DateOnly today)
    {
        var baselineInfo = baseline is null
            ? "No baseline established yet — this member is still being learned."
            : $"{baseline.PeriodDays}-day — Steps: {baseline.AvgSteps}±{baseline.StdDevSteps}, " +
              $"Resting HR: {baseline.AvgRestingHeartRate}±{baseline.StdDevHeartRate}, " +
              $"Sleep: {baseline.AvgSleepMinutes} min";

        var recentSummary = MedicalPromptBlocks.DailyLines(recentLogs, take: 3, today);

        return $"""
            {AlertInstructions}

            {memberContext}

            --- Alert ---
            Type: {alert.AlertType}
            Severity: {alert.Severity}
            Title: {alert.Title}
            Message: {alert.Message}
            Triggered: {alert.TriggeredDate:yyyy-MM-dd HH:mm} UTC
            Metric values: {alert.MetricValues ?? "none"}

            --- Baseline ---
            {baselineInfo}

            --- Recent activity (last 3 days, oldest first) ---
            {recentSummary}
            """;
    }

    private static string BuildBaselinePrompt(
        string memberContext,
        IEnumerable<PatternBaseline> baselines,
        IEnumerable<ActivityLog> recentLogs,
        DateOnly today)
    {
        var baselineLines = baselines.Select(b =>
            $"{b.PeriodDays}-day — Steps: {b.AvgSteps}±{b.StdDevSteps}, " +
            $"HR: {b.AvgRestingHeartRate}±{b.StdDevHeartRate}, Sleep: {b.AvgSleepMinutes} min" +
            SleepWindow(b));

        return $"""
            {BaselineInstructions}

            {memberContext}

            --- Baselines ---
            {string.Join("\n", baselineLines)}

            --- Recent activity (last 7 days, oldest first) ---
            {MedicalPromptBlocks.DailyLines(recentLogs, take: 7, today)}
            """;
    }

    private static string BuildProvisionalPrompt(
        string memberContext,
        PatternBaseline baseline,
        IEnumerable<ActivityLog> recentLogs,
        DateOnly today)
    {
        return $"""
            {ProvisionalInstructions}

            {memberContext}

            --- Provisional baseline ---
            {baseline.PeriodDays}-day (provisional) — Steps: {baseline.AvgSteps}±{baseline.StdDevSteps}, HR: {baseline.AvgRestingHeartRate}±{baseline.StdDevHeartRate}, Sleep: {baseline.AvgSleepMinutes} min{SleepWindow(baseline)}

            --- Recent activity (last 7 days, oldest first) ---
            {MedicalPromptBlocks.DailyLines(recentLogs, take: 7, today)}
            """;
    }

    private static string BuildLearningPrompt(
        string memberContext, IReadOnlyCollection<ActivityLog> recentLogs, DateOnly today)
    {
        var daysObserved = recentLogs.Select(l => l.Date).Distinct().Count();

        return $"""
            {LearningInstructions}

            {memberContext}

            --- Observation so far ---
            Days with data in the last 14: {daysObserved}
            No baseline has been established yet.

            --- Daily readings ---
            {MedicalPromptBlocks.DailyLines(recentLogs, take: 14, today)}
            """;
    }

    // Member context, note flattening, and daily-reading lines live in MedicalPromptBlocks,
    // shared with the digest pipeline so the minimisation and injection-framing rules cannot
    // drift between the private model's callers.

    /// <summary>
    /// Typical bedtime and wake time, when the baseline has settled on them. Stored in UTC, and said
    /// so — an unlabelled "22:40" invites the model to reason about a local evening it cannot see.
    /// </summary>
    private static string SleepWindow(PatternBaseline baseline) =>
        baseline.TypicalBedtime is TimeOnly bedtime && baseline.TypicalWakeTime is TimeOnly wake
            ? $", Typical sleep window: {bedtime:HH\\:mm}–{wake:HH\\:mm} UTC"
            : string.Empty;

    // ── MedGemma response shapes ────────────────────────────────────────────────
    // Internal, not Application/DTOs: these describe the private model's reply, not the public
    // API contract (AlertInsightResponse etc. already own that boundary). Internal rather than
    // private so IMedicalAiService.GenerateStructuredAsync<T> can be exercised directly in tests.

    internal sealed record AlertAiResponse
    {
        public required string Explanation { get; init; }
        public required string RecommendedAction { get; init; }
    }

    /// <summary>Shared by the trend, provisional and learning prompts — all three ask for the same
    /// summary-plus-findings shape, even though what belongs in each field differs by prompt.</summary>
    internal sealed record BaselineAiResponse
    {
        public required string Summary { get; init; }
        public required IReadOnlyList<string> KeyFindings { get; init; }
    }

}
