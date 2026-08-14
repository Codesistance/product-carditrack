using System.Text.Json;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Infrastructure.Services.PromptContext;
using CardiTrack.Infrastructure.Settings;
using Microsoft.Extensions.Caching.Distributed;

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

    /// <summary><c>CARDITRACK_ALERT_PROMPT</c> — explains a fired alert to a caregiver.</summary>
    private const string AlertInstructions =
        MedicalPromptBlocks.Tone + MedicalPromptBlocks.Pronouns + """
        You are a medical AI assistant analysing a health alert for a non-clinical caregiver.

        Respond with:
        - explanation: what this alert means clinically, with likely contributing factors from
          the recent data.
        - recommendedAction: a concise recommended action for the caregiver.

        Keep both fields factual and concise. Never diagnose — flag for review.
        """ + MedicalPromptBlocks.ContextGuardrail;

    /// <summary><c>CARDITRACK_BASELINE_PROMPT</c> — trend analysis once a baseline exists.</summary>
    private const string BaselineInstructions =
        MedicalPromptBlocks.Tone + MedicalPromptBlocks.Pronouns + """
        You are a medical AI assistant performing a health trend analysis for a non-clinical caregiver.

        Respond with:
        - summary: the member's overall health trends, including any patterns that warrant
          caregiver attention.
        - keyFindings: short strings, one per key finding.

        Keep the response factual. Never diagnose — flag for review.
        """ + MedicalPromptBlocks.ContextGuardrail;

    /// <summary>
    /// <c>CARDITRACK_LEARNING_PROMPT</c> — the first weeks, before a baseline exists. Nothing can be
    /// called unusual yet because there is no normal to compare against, so this asks for a picture
    /// of what has been observed rather than an assessment of deviation.
    /// </summary>
    private const string LearningInstructions =
        MedicalPromptBlocks.Tone + MedicalPromptBlocks.Pronouns + """
        You are a medical AI assistant describing what has been observed about a member so far.
        There is not yet enough history to know this person's normal, so call nothing unusual,
        elevated, low, or a deviation — there is nothing yet to deviate from.

        Respond with:
        - summary: the daily rhythm shown so far, and what is still needed for a reliable
          picture of this member.
        - keyFindings: short strings, one per key observation.

        Be plain and encouraging about the process. Never diagnose.
        """ + MedicalPromptBlocks.ContextGuardrail;

    /// <summary>
    /// <c>CARDITRACK_PROVISIONAL_PROMPT</c> — a provisional (sub-30-day) baseline exists. There is
    /// an early picture to compare against, but not an established normal, so the framing sits
    /// between the learning prompt (no comparisons at all) and the trend prompt (confident
    /// comparisons): tentative comparisons, no alarm on the strength of a short window.
    /// </summary>
    private const string ProvisionalInstructions =
        MedicalPromptBlocks.Tone + MedicalPromptBlocks.Pronouns + """
        You are a medical AI assistant giving an early health reading for a non-clinical caregiver.
        The baseline is provisional — under 30 days of history — so a comparison against it is
        an early impression, not an established pattern. Phrase findings tentatively ("so far",
        "appears", "early signs"), and do not treat a deviation from so short a window as cause
        for alarm.

        Respond with:
        - summary: what the early data suggests, and what will become clearer once the full
          30-day baseline is established.
        - keyFindings: short strings, one per key observation.

        Keep the response factual. Never diagnose — flag for review.
        """ + MedicalPromptBlocks.ContextGuardrail;

    /// <summary>
    /// <c>CARDITRACK_CURRENT_STATUS_PROMPT</c> — a single empathetic line for the Dashboard's
    /// hero card. Distinct from the other three: this is ambient, ever-present copy shown on
    /// every dashboard view rather than something a caregiver deliberately opened, so it asks for
    /// one short, warm sentence rather than a structured explanation.
    /// </summary>
    /// <remarks>
    /// Kept short on purpose, and shorter than its siblings. This is the only prompt on a request
    /// path a caregiver is waiting on, and MedGemma processes a prompt at a few tens of tokens per
    /// second against CPU — so on this endpoint every token of instruction is paid in latency, on
    /// every call without exception. The fixed prefix buys nothing back: Gemma 3's sliding-window
    /// attention stops llama.cpp restoring a KV checkpoint, so a warm instance with the model
    /// resident still reprocesses from token zero (docs/llm_design.md). Asking for 19 tokens of
    /// output behind 666 tokens of prompt spent ~9.8 s of a ~11 s call just reading the question.
    /// One example per field is enough when the response shape is already grammar-constrained by
    /// the JSON schema; three were paying for themselves in seconds.
    /// <see cref="StatusPromptBudget"/> keeps it that way.
    /// </remarks>
    private const string CurrentStatusInstructions = MedicalPromptBlocks.Tone + """
        Describe this person's current status to their caregiver.

        Third person, naming them {{NAME}} exactly as written; it stands in for their real
        name. Never use clinical terms (elevated, abnormal, deviation, diagnosis) and never
        suggest a medical cause. Match the tone to the severity given, gently more attentive
        as it rises.

        Respond with:
        - headline: two to five words, sentence case, no full stop, no name
        - message: one sentence under 15 words.

        No preamble, no quotation marks, no explanation.
        """ + MedicalPromptBlocks.ContextGuardrailNotesOnly;

    /// <summary>
    /// Ceiling on <see cref="CurrentStatusInstructions"/>, in characters — the fixed half of the
    /// status prompt, tone block included.
    /// </summary>
    /// <remarks>
    /// The instructions rather than the rendered prompt, because the rendered prompt legitimately
    /// varies: a caregiver note adds up to <see cref="MedicalPromptBlocks.MaxNoteLength"/>
    /// characters and that is the caregiver's to spend, not this file's. What a budget can hold is
    /// the part that is written here — which is also the part that creeps, one helpful
    /// clarification at a time, each defensible and none alone worth a second. Prompt length is
    /// latency on this path in a way it is not on the digest or assess jobs, and the test that
    /// pins this is the only thing that will notice.
    /// </remarks>
    internal const int StatusPromptBudget = 1_050;

    /// <summary>Exposed for the budget test — the instructions themselves stay private.</summary>
    internal static int CurrentStatusInstructionsLength => CurrentStatusInstructions.Length;

    /// <summary>
    /// This is a "friendly vibe" line, not the alerting system, so a message up to this old is an
    /// acceptable trade-off against calling MedGemma on every dashboard load — slightly longer
    /// than the Worker's default 10-minute sync cadence, so a cache hit is the common case
    /// between syncs.
    /// </summary>
    private static readonly TimeSpan CurrentStatusTtl = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Ceiling on the punchy note. Well past the two-to-five words asked for — this is the guard
    /// against a model that answers with a sentence, not the length being aimed at.
    /// </summary>
    private const int MaxStatusHeadlineLength = 40;

    private readonly IMedicalAiService _medicalAi;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICardiMemberAccessService _access;
    private readonly MemberContextComposer _memberContext;
    private readonly IDistributedCache _cache;
    private readonly PrivateAiSettings _aiSettings;

    public HealthInsightService(
        IMedicalAiService medicalAi,
        IUnitOfWork unitOfWork,
        ICardiMemberAccessService access,
        MemberContextComposer memberContext,
        IDistributedCache cache,
        PrivateAiSettings aiSettings)
    {
        _medicalAi = medicalAi;
        _unitOfWork = unitOfWork;
        _access = access;
        _memberContext = memberContext;
        _cache = cache;
        _aiSettings = aiSettings;
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

        return new AlertInsightResponse
        {
            AlertId = alertId,
            Explanation = aiResponse.Explanation,
            Severity = alert.Severity,
            RecommendedAction = aiResponse.RecommendedAction
        };
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

    /// <summary>Cache payload — carries its own generation time so a cache hit can report when
    /// the line was actually generated rather than the moment it happened to be read.</summary>
    private sealed record CachedStatus(string? Headline, string Message, DateTimeOffset GeneratedAt);

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

    /// <summary>"Nothing to say yet" — the contract's own way of saying it, so every path that
    /// declines to generate answers in the shape the dashboard already handles.</summary>
    private static CurrentStatusMessageResponse NoStatusMessage() =>
        new() { Headline = null, Message = null, GeneratedAt = DateTimeOffset.UtcNow };

    public async Task<CurrentStatusMessageResponse> GetCurrentStatusMessageAsync(
        Guid requestingUserId, Guid cardiMemberId, CancellationToken ct = default)
    {
        await _access.RequireViewAccessAsync(requestingUserId, cardiMemberId, ct);

        var cacheKey = DashboardStatusCacheKey.For(cardiMemberId);
        var cachedJson = await _cache.GetStringAsync(cacheKey, ct);
        if (cachedJson is not null)
        {
            var cached = JsonSerializer.Deserialize<CachedStatus>(cachedJson)!;
            return new CurrentStatusMessageResponse
            {
                Headline = cached.Headline,
                Message = cached.Message,
                GeneratedAt = cached.GeneratedAt,
            };
        }

        // The same guard the three background generators use before spending a model call
        // (StatisticalAlertService, RealtimeAssessmentService, InactivityDetectionService). It was
        // missing here, so the one surface that fires on every dashboard load was also the one
        // that would generate for a member nobody is watching. The mobile client skips these tiers
        // itself, which is why this never showed up in practice — but the endpoint is reachable
        // directly, and a client-side skip is not a server-side rule.
        var member = await _unitOfWork.CardiMembers.GetByIdAsync(cardiMemberId);
        if (member is null || !member.IsActive || member.IsMonitoringPaused(DateTime.UtcNow))
            return NoStatusMessage();

        // Claims the member's generation slot. The get-then-set is not atomic — IDistributedCache
        // exposes no set-if-absent — so this is a fan-out damper, not a mutex, the same trade
        // ManualDeviceSyncService documents. What it stops is the case that actually happens: a
        // cold cache and several dashboards opening at once, each of which would otherwise start
        // its own tens-of-seconds generation because the cache is only written at the end. A
        // caller that loses the slot answers "nothing to say yet" straight away rather than
        // queueing behind a generation whose result it would not wait for anyway.
        var claimKey = $"dashboard-status-pending:{cardiMemberId}";
        if (await _cache.GetStringAsync(claimKey, ct) is not null)
            return NoStatusMessage();

        var budget = TimeSpan.FromSeconds(_aiSettings.CurrentStatusBudgetSeconds);
        await _cache.SetStringAsync(claimKey, "1",
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = budget }, ct);

        // The budget covers the whole generation, not just the model call, because what it
        // promises is a response — the caller's own token still cancels this, it only ever
        // tightens the deadline.
        using var budgeted = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budgeted.CancelAfter(budget);

        try
        {
            return await GenerateCurrentStatusAsync(member, cardiMemberId, cacheKey, budgeted.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Ours expired, not the caller's. Answering without a line beats holding the phone
            // past its own 30 s timeout, where it gets a socket error instead of the static
            // per-tier copy this response shape exists to fall back to. Nothing is cached, so the
            // next load tries again — and the claim above expires with the budget.
            return NoStatusMessage();
        }
    }

    private async Task<CurrentStatusMessageResponse> GenerateCurrentStatusAsync(
        CardiMember member, Guid cardiMemberId, string cacheKey, CancellationToken ct)
    {
        // activeOnly filters on IsActive, which a resolved alert keeps — resolution ends the
        // episode without deactivating the row. Without the second filter this line was reading
        // closed episodes as live ones, so the hero line went on sounding concerned about something
        // the assessor had already called over, while the status colour beside it had moved on.
        // Same read as DashboardService's.
        var unresolvedAlerts = (await _unitOfWork.Alerts.GetByCardiMemberAsync(cardiMemberId, true))
            .Where(a => !a.IsResolved)
            .ToList();
        var severity = unresolvedAlerts.Count == 0
            ? "green"
            : unresolvedAlerts.Max(a => a.Severity).ToString().ToLowerInvariant();

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var recentLogs = await _unitOfWork.ActivityLogs
            .GetByCardiMemberAndDateRangeAsync(cardiMemberId, today.AddDays(-2), today);

        var memberContext = await ComposeMemberContextAsync(
            member, cardiMemberId, today, PromptPurpose.CurrentStatus, ct);

        var prompt = BuildCurrentStatusPrompt(memberContext, severity, unresolvedAlerts, recentLogs, today);
        var aiResponse = await _medicalAi.GenerateStructuredAsync<CurrentStatusAiResponse>(prompt, ct);

        // Resolved before the cache, not after: the cached copy is what the next fifteen minutes
        // of dashboard views read, so caching an unresolved placeholder would show braces to a
        // caregiver long after the call that produced them. An unresolvable placeholder falls
        // through to the empty-message path below, which the response contract already treats as
        // "nothing to say yet" — the dashboard keeps its per-tier headline and says nothing false.
        var name = NamePlaceholder.FirstName(member.Name);
        var message = NamePlaceholder.Resolve(aiResponse.Message.Trim(), name) ?? string.Empty;
        if (NamePlaceholder.IsPresentIn(message))
            message = string.Empty;
        // A missing headline does not sink the sentence: the dashboard keeps its per-tier headline
        // and still gets the live line under it.
        var headline = CleanStatusHeadline(aiResponse.Headline);
        var generatedAt = DateTimeOffset.UtcNow;

        // An empty response reads as a transient model hiccup, not a stable "nothing to say" —
        // caching it would strand the dashboard with no message for the rest of the TTL window
        // instead of retrying on the next call. The response contract already treats a null
        // Message as "nothing to say yet", so an empty string is never returned either.
        if (!string.IsNullOrWhiteSpace(message))
        {
            var toCache = JsonSerializer.Serialize(new CachedStatus(headline, message, generatedAt));
            await _cache.SetStringAsync(cacheKey, toCache,
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = CurrentStatusTtl }, ct);
        }

        return new CurrentStatusMessageResponse
        {
            Headline = string.IsNullOrWhiteSpace(message) ? null : headline,
            Message = string.IsNullOrWhiteSpace(message) ? null : message,
            GeneratedAt = generatedAt,
        };
    }

    private static string BuildCurrentStatusPrompt(
        string memberContext,
        string severity,
        IReadOnlyCollection<Alert> unresolvedAlerts,
        IEnumerable<ActivityLog> recentLogs,
        DateOnly today)
    {
        // Titles only. The type and severity of each alert are what the tier above is computed
        // from, so repeating them per alert tells the model nothing it has not been told — and on
        // this path a line it does not need is a line it still has to read.
        var alertContext = unresolvedAlerts.Count == 0
            ? "No unresolved alerts."
            : string.Join("\n", unresolvedAlerts.Select(a => $"- {a.Title}"));

        return $"""
            {CurrentStatusInstructions}

            {memberContext}

            --- Current severity tier ---
            {severity}

            --- Unresolved alerts driving this tier ---
            {alertContext}

            --- Recent activity (last 3 days, oldest first) ---
            {StatusActivityLines(recentLogs, today)}
            """;
    }

    /// <summary>
    /// The same daily readings <see cref="MedicalPromptBlocks.DailyLines"/> renders, said in about
    /// a third of the tokens.
    /// </summary>
    /// <remarks>
    /// Not a simplification of that helper — a different question. Its day labels are long because
    /// the digest and trend prompts attribute figures to named days and got that wrong in ways
    /// worth spelling out to the model; those prompts run on jobs that can afford the words. This
    /// one is asked for a single sentence about how someone is doing today, where the shape of the
    /// last three days is context rather than something to be quoted back. So the labels shrink to
    /// what still has to be unambiguous: which day, and that today is not finished yet.
    /// </remarks>
    private static string StatusActivityLines(IEnumerable<ActivityLog> logs, DateOnly today)
    {
        var lines = logs
            .TakeLast(3)
            .Select(l =>
            {
                var label = (today.DayNumber - l.Date.DayNumber) switch
                {
                    <= 0 => "Today so far (partial)",
                    1 => "Yesterday",
                    var days => $"{days} days ago",
                };
                return $"  {label}: steps={l.Steps}, HR={l.RestingHeartRate}, "
                       + $"sleep(prev night)={l.SleepMinutes}min";
            })
            .ToList();

        return lines.Count > 0 ? string.Join("\n", lines) : "No recent activity data.";
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

    internal sealed record CurrentStatusAiResponse
    {
        /// <summary>The punchy note above the sentence — see <see cref="CleanStatusHeadline"/>
        /// for what happens to one that arrives as prose.</summary>
        public string? Headline { get; init; }

        public required string Message { get; init; }
    }
}
