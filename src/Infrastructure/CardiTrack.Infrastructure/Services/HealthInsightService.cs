using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Services.PromptContext;

namespace CardiTrack.Infrastructure.Services;

public class HealthInsightService : IHealthInsightService
{
    /// <summary>
    /// The 30-day baseline is the one <c>DashboardService</c> uses to decide whether a member is still
    /// being learned, so the same period decides which prompt this service sends.
    /// </summary>
    private const int PrimaryBaselinePeriodDays = 30;

    /// <summary>
    /// Which version of the alert brief wrote a stored explanation. Bumped when
    /// <see cref="AlertInstructions"/> changes in a way that should reach alerts already explained
    /// — the only thing that earns an alert a second model call.
    /// </summary>
    internal const int AlertPromptVersion = 1;

    /// <summary>
    /// The same, for the three baseline briefs. They move together because which one is sent is
    /// decided by the member's baseline state rather than by the caller, and versioning them apart
    /// would leave a member's row claiming a version whose brief it was not written by.
    /// </summary>
    internal const int BaselinePromptVersion = 2;

    /// <summary>
    /// How recently a baseline insight has to have been written before a pass skips it. An hour,
    /// matching the digest's own regeneration floor: the two ride the same pass, and a floor that
    /// disagreed with the digest's would either regenerate a member the digest skipped or leave
    /// this one behind for a whole pass.
    /// </summary>
    internal static readonly TimeSpan BaselineRegenerationFloor = TimeSpan.FromHours(1);

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
        - explanation: what this alert means in the recent readings.
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
        MedicalPromptBlocks.Tone + """
        You are telling one family whether anything about the person they watch over needs their
        attention this week.

        Everything below was worked out from this person's own measurements against their
        established baseline before you saw it, and only the metrics that moved away from their
        usual by more than their own normal variation are listed as having moved. Say what the figures say. Never work out a comparison, a
        percentage or a direction yourself, and never introduce a number that is not in front of
        you. Do not call a metric unchanged unless it is named as steady below.

        """ + MedicalPromptBlocks.CaregiverRegister + """
        Respond with:
        - summary: two or three sentences answering whether this is something to pay attention to.
          Lead with the movement that matters most, say which way it went and roughly how far in
          the words the figures use, and say plainly where the rest has held steady.
        - keyFindings: up to three short lines, each naming one movement worth noticing. One line
          per movement, never one per metric — a metric that has not moved is not a finding.

        Never name a condition, a diagnosis or a treatment. Never give a score, a probability, a
        risk level or a prediction of what will happen next. Do not pad the list to three, and do
        not look for something to report where the figures show nothing.
        """ + MedicalPromptBlocks.ContextGuardrail;

    /// <summary>
    /// <c>CARDITRACK_LEARNING_PROMPT</c> — the first weeks, before a baseline exists. Nothing can be
    /// called unusual yet because there is no normal to compare against, so this asks for a picture
    /// of what has been observed rather than a judgement. The register is
    /// <see cref="MedicalPromptBlocks.CaregiverRegister"/>. The words it must not use are not
    /// listed: MedGemma would echo them.
    /// </summary>
    private const string LearningInstructions =
        MedicalPromptBlocks.Tone + """
        Describe what the readings have shown so far.
        There is not yet enough history to know this person's normal, so call nothing unusual.

        """ + MedicalPromptBlocks.CaregiverRegister + """
        Respond with:
        - summary: the daily rhythm shown so far, and what is still needed for a reliable
          picture of this member.
        - keyFindings: up to three short strings, one per key observation.
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
        MedicalPromptBlocks.Tone + """
        Describe an early reading against this short window.
        The baseline is provisional — under 30 days of history — so a comparison is an impression, not an established pattern.
        Do not treat so short a window as settled.

        """ + MedicalPromptBlocks.CaregiverRegister + """
        Respond with:
        - summary: what the early data suggests, and what will become clearer once the full
          30-day baseline is established.
        - keyFindings: up to three short strings, one per key observation.
        """ + MedicalPromptBlocks.ContextGuardrail;

    // The current-status prompt, its budget and the generation path moved to
    // StatusLineGenerationService with the batch move: the line is generated by the pipeline's
    // digest and assess passes and persisted per member, and this service only reads the row.

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

    /// <summary>"Not explained yet" — the shape every declining path returns, so a client never
    /// has to tell an absent explanation from a failed one.</summary>
    private static AlertInsightResponse NoAlertInsight(Guid alertId, AlertSeverity severity) => new()
    {
        AlertId = alertId,
        Explanation = string.Empty,
        Severity = severity,
        RecommendedAction = string.Empty,
    };

    /// <summary>
    /// Read-only since the batch move, for the same reason as <see cref="GetAdviseAsync"/>: the
    /// explanation is written by the pass that raised the alert
    /// (<see cref="RegenerateAlertInsightAsync"/>) and persisted against it, so opening an alert
    /// costs one indexed lookup. It used to build a prompt and call MedGemma inline, which meant a
    /// caregiver tapping an alert paid a cold start — or, when the shared service was catching up,
    /// a 503 — for text the pipeline could have written in a pass it was already running.
    /// </summary>
    public async Task<AlertInsightResponse> AnalyzeAlertAsync(
        Guid requestingUserId, Guid alertId, CancellationToken ct = default)
    {
        // An alert is reachable only through its CardiMember. Both "no such alert" and
        // "not your alert" report the same not-found so the alert id cannot be probed.
        var alert = await _unitOfWork.Alerts.GetByIdWithCardiMemberAsync(alertId);
        if (alert is null || !await _access.HasViewAccessAsync(requestingUserId, alert.CardiMemberId, ct))
            throw new KeyNotFoundException($"Alert {alertId} not found.");

        var stored = await _unitOfWork.MemberInsights.GetForAlertAsync(alertId);
        if (!InsightServability.IsServable(stored, DateTime.UtcNow))
            return NoAlertInsight(alertId, alert.Severity);

        return new AlertInsightResponse
        {
            AlertId = alertId,
            Explanation = stored.Summary,
            // From the alert rather than the stored row: severity is the alert's own fact, and an
            // insight written before a re-judgement must not report the older word for it.
            Severity = alert.Severity,
            RecommendedAction = stored.RecommendedAction ?? string.Empty,
        };
    }

    /// <summary>
    /// Writes the explanation for one alert, in the pipeline pass that raised it. Returns whether
    /// a row was written.
    /// </summary>
    /// <remarks>
    /// Never regenerated once written by the current brief: the alert it explains describes a
    /// fixed moment and does not change afterwards, so a second pass over the same row would spend
    /// a model call to say the same thing. A brief change does earn a rewrite — that is what
    /// <see cref="AlertPromptVersion"/> is for.
    /// </remarks>
    public async Task<bool> RegenerateAlertInsightAsync(Guid alertId, CancellationToken ct = default)
    {
        var alert = await _unitOfWork.Alerts.GetByIdWithCardiMemberAsync(alertId);
        if (alert is null)
            return false;

        var existing = await _unitOfWork.MemberInsights.GetForAlertAsync(alertId);
        if (existing is not null && existing.PromptVersion >= AlertPromptVersion)
            return false;

        // Anchored to the alert, not to now. The backfill in StatisticalAlertService can reach a
        // standing alert weeks after it fired, and a window taken from today would explain that
        // alert with readings from a fortnight it had nothing to do with — an explanation of one
        // event written from another week's data is worse than no explanation at all. For an alert
        // raised moments ago, which is the common case, this is the same window as before.
        var to = DateOnly.FromDateTime(AsUtc(alert.TriggeredDate));
        var from = to.AddDays(-7);
        var recentLogs = await _unitOfWork.ActivityLogs
            .GetByCardiMemberAndDateRangeAsync(alert.CardiMemberId, from, to);

        var member = await _unitOfWork.CardiMembers.GetByIdAsync(alert.CardiMemberId);

        // As of the alert, for the reason the window above is. The backfill can reach an alert
        // weeks after it fired, and by then the member's usual has moved — explaining their
        // stamped readings against a newer normal describes a departure that may no longer be
        // one, or misses one that was. The readings and the usual they are judged against have
        // to come from the same moment or the narrative is about neither.
        var baseline = await _unitOfWork.PatternBaselines.GetAsOfByCardiMemberAsync(
            alert.CardiMemberId, PrimaryBaselinePeriodDays, AsUtc(alert.TriggeredDate));

        var memberContext = await ComposeMemberContextAsync(
            member, alert.CardiMemberId, to, PromptPurpose.AlertInsight, ct);

        var prompt = BuildAlertPrompt(alert, memberContext, recentLogs, baseline, to);
        var aiResponse = await _medicalAi.GenerateStructuredAsync<AlertAiResponse>(prompt, ct);

        var name = NamePlaceholder.FirstName(member?.Name);
        var explanation = CaregiverFacingInsight(aiResponse.Explanation, name);

        // An explanation the guards emptied is not an explanation, and storing it would leave the
        // screen showing a heading over nothing. Withheld entirely, the same stance
        // AdviseGenerationService takes on a suggestion with no grounding.
        if (explanation.Length == 0)
            return false;

        var row = existing ?? new MemberInsight
        {
            CardiMemberId = alert.CardiMemberId,
            Scope = InsightScope.Alert,
            AlertId = alertId,
        };

        // Fitted to the columns for the reason InsightLimits gives: a save that fails on length
        // costs the member the explanation and makes the row a permanent backfill candidate.
        row.Summary = InsightLimits.Fit(explanation, InsightLimits.Summary)!;
        row.RecommendedAction = InsightLimits.Fit(
            CaregiverFacingInsight(aiResponse.RecommendedAction, name), InsightLimits.RecommendedAction);
        row.BaselinePeriodDays = baseline?.PeriodDays;
        row.GeneratedAtUtc = DateTime.UtcNow;
        row.PromptVersion = AlertPromptVersion;

        if (existing is null)
            await _unitOfWork.MemberInsights.AddAsync(row);

        await _unitOfWork.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// Substitutes <see cref="NamePlaceholder.Token"/> when a name is on file. Leftover braces
    /// are dropped rather than returned: the status line and the digest already refuse to show
    /// them, and an insight that still says <c>CardiTrackCardiMember</c> is worse than an empty field.
    /// A named condition is dropped the same way — this path has no rewrite step to strip one.
    /// </summary>
    private static string ResolvedOrEmpty(string? text, string? name)
    {
        var resolved = NamePlaceholder.Resolve(text, name) ?? string.Empty;
        if (NamePlaceholder.IsPresentIn(resolved))
            return string.Empty;

        // Trimmed, and whitespace treated as nothing at all. A reply of three spaces passes the
        // register guard (there is no condition in it to name) and would otherwise be stored as a
        // blank row stamped with the current prompt version — which every later pass then skips as
        // already done, while the read path hides it for having no text. The alert would never be
        // explained again.
        return string.IsNullOrWhiteSpace(resolved) ? string.Empty : resolved.Trim();
    }

    private static string CaregiverFacingInsight(string? text, string? name)
    {
        var resolved = ResolvedOrEmpty(text, name);
        return JournalRegisterGuards.NamesACondition(resolved) is null ? resolved : string.Empty;
    }

    /// <summary>"Nothing read yet" — the learning state, which is also the honest answer before
    /// the first pass has run for this member.</summary>
    private static BaselineInsightResponse NoBaselineInsight(Guid cardiMemberId) => new()
    {
        CardiMemberId = cardiMemberId,
        Summary = string.Empty,
        KeyFindings = [],
        IsLearning = true,
        GeneratedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>
    /// Read-only since the batch move. The reading of the member against their own baseline is
    /// written by the digest pass (<see cref="RegenerateBaselineInsightAsync"/>) and persisted per
    /// member; this serves the latest row and never calls the model.
    /// </summary>
    public async Task<BaselineInsightResponse> AnalyzeBaselineAsync(
        Guid requestingUserId, Guid cardiMemberId, CancellationToken ct = default)
    {
        await _access.RequireViewAccessAsync(requestingUserId, cardiMemberId, ct);

        // The same guard the dashboard and member-detail paths apply before showing this block,
        // and GetAdviseAsync before serving its row. Without it the dedicated endpoint disagreed
        // with the screens: a reading of how someone is doing, for monitoring that has stopped.
        if (!await IsBeingWatchedAsync(cardiMemberId))
            return NoBaselineInsight(cardiMemberId);

        var stored = await _unitOfWork.MemberInsights.GetByScopeAsync(cardiMemberId, InsightScope.Baseline);
        if (!InsightServability.IsServable(stored, DateTime.UtcNow))
            return NoBaselineInsight(cardiMemberId);

        return new BaselineInsightResponse
        {
            CardiMemberId = cardiMemberId,
            Summary = stored.Summary,
            KeyFindings = SplitFindings(stored.KeyFindings),
            IsLearning = stored.IsLearning,
            IsProvisional = stored.IsProvisional,
            BaselinePeriodDays = stored.BaselinePeriodDays,
            GeneratedAt = new DateTimeOffset(DateTime.SpecifyKind(stored.GeneratedAtUtc, DateTimeKind.Utc)),
        };
    }

    /// <summary>
    /// The stored findings back as a list. Newline-joined on the way in, so a finding that somehow
    /// carried a blank line does not come back as an empty bullet.
    /// </summary>
    private static IReadOnlyList<string> SplitFindings(string? stored) =>
        string.IsNullOrWhiteSpace(stored)
            ? []
            : stored.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Writes the member's reading against their own baseline, in the digest pass. Returns whether
    /// a row was written.
    /// </summary>
    /// <remarks>
    /// Unlike the alert scope this is rewritten as the picture moves, so it is gated on age and
    /// brief version rather than on existence: a member whose readings have not moved since the
    /// last pass is skipped before any model call, the same two gates
    /// <c>DigestGenerationService</c> applies for the same reason.
    /// </remarks>
    public async Task<bool> RegenerateBaselineInsightAsync(
        Guid cardiMemberId, CancellationToken ct = default)
    {
        var existing = await _unitOfWork.MemberInsights.GetByScopeAsync(cardiMemberId, InsightScope.Baseline);
        if (existing is not null
            && existing.PromptVersion >= BaselinePromptVersion
            && DateTime.UtcNow - existing.GeneratedAtUtc < BaselineRegenerationFloor)
        {
            return false;
        }

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

        // The sleep window below is two learned times of day, and a time of day means nothing
        // until it says whose clock it is on. It used to be handed over labelled UTC, which is
        // honest but leaves the model reading a household's bedtime off Greenwich's clock; the
        // Daybook now speaks the member's own local time, and two prompts showing two different
        // faces for the same baseline field is the drift MemberAnchorTimeZone exists to prevent.
        //
        // Only the two baseline prompts carry that window, and resolving the zone walks the
        // member's caregiver links and then reads a row per link. A member still being learned has
        // no window to put on a clock, so on that path the walk would be queries run to be thrown
        // away — and the learning path is the one every new member takes for their first month.
        var timeZone = isLearning
            ? null
            : await MemberAnchorTimeZone.ResolveAsync(_unitOfWork, cardiMemberId);

        // What has actually moved, decided here rather than by the model. Only the established
        // path gets this: the other two are honest readouts of a picture still forming, and there
        // is no settled usual to measure a departure against.
        //
        // Through yesterday, not today. Today's ActivityLog row holds however far through the day
        // the sync has got — a morning's steps and no active minutes yet — and it is one of seven
        // in the average, so including it reports a departure that is only the clock.
        // TrendInterpretationService ends its window a day back for the same reason.
        var movements = BaselineMovementCalculator.Compute(
            recentLogs, primaryBaseline, to.AddDays(-1));

        if (primaryBaseline is not null && movements is { HasAnythingToSay: false })
        {
            // Nothing has moved, so there is nothing to say and no call worth paying for. The row
            // comes down rather than being left to age out: this card is read as "something wants
            // your attention", and InsightServability would go on serving the last thing that did
            // for three more days after it stopped being true.
            //
            // Only on positive evidence that nothing is off, which is stricter than having
            // looked. A week with heart-rate readings and no step readings has judged something
            // and said nothing whatever about steps, so retracting a standing card about this
            // member's steps on the strength of it would be answering a question it never asked.
            // Where the evidence is partial the row stays and ages out of InsightServability as
            // it did before it could be removed at all.
            if (movements.ShowsNothingIsOff && existing is not null)
            {
                _unitOfWork.MemberInsights.Remove(existing);
                await _unitOfWork.SaveChangesAsync();
            }

            return false;
        }

        var prompt = (primaryBaseline, provisionalBaseline) switch
        {
            (not null, _) => BuildBaselinePrompt(
                memberContext, baselines, movements!, to, timeZone),
            (null, not null) => BuildProvisionalPrompt(
                memberContext, provisionalBaseline, recentLogs, to, timeZone),
            _ => BuildLearningPrompt(memberContext, recentLogs, to),
        };

        var aiResponse = await _medicalAi.GenerateStructuredAsync<BaselineAiResponse>(prompt, ct);

        // The same placeholder guard the alert path applies. These three briefs never mention
        // CardiTrackCardiMember — only the alert one does — so a token that reaches a caregiver
        // unresolved is worse than an empty field wherever it happens.
        var name = NamePlaceholder.FirstName(member?.Name);
        var summary = ResolvedOrEmpty(aiResponse.Summary, name);
        if (summary.Length == 0)
            return false;

        var findings = aiResponse.KeyFindings
            .Select(finding => ResolvedOrEmpty(finding, name))
            .Where(finding => finding.Length > 0)
            .Take(InsightLimits.MaxFindings)
            .ToList();

        var row = existing ?? new MemberInsight
        {
            CardiMemberId = cardiMemberId,
            Scope = InsightScope.Baseline,
        };

        row.Summary = InsightLimits.Fit(summary, InsightLimits.Summary)!;
        row.KeyFindings = InsightLimits.JoinFindings(findings);
        row.IsLearning = isLearning;
        row.IsProvisional = provisionalBaseline is not null;
        row.BaselinePeriodDays = (primaryBaseline ?? provisionalBaseline)?.PeriodDays;
        row.GeneratedAtUtc = DateTime.UtcNow;
        row.PromptVersion = BaselinePromptVersion;

        if (existing is null)
            await _unitOfWork.MemberInsights.AddAsync(row);

        await _unitOfWork.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// Read-only, like the alert and baseline reads: the narrative is written by the daily
    /// <c>--job trend</c> pass and persisted per member.
    /// </summary>
    public async Task<TrendInsightResponse> GetTrendAsync(
        Guid requestingUserId, Guid cardiMemberId, CancellationToken ct = default)
    {
        await _access.RequireViewAccessAsync(requestingUserId, cardiMemberId, ct);

        if (!await IsBeingWatchedAsync(cardiMemberId))
            return NoTrend(cardiMemberId);

        var stored = await _unitOfWork.MemberInsights.GetByScopeAsync(cardiMemberId, InsightScope.Trend);
        if (!InsightServability.IsServable(stored, DateTime.UtcNow))
            return NoTrend(cardiMemberId);

        return new TrendInsightResponse
        {
            CardiMemberId = cardiMemberId,
            Narrative = stored.Summary,
            KeyFindings = MemberInsightComposer.Findings(stored),
            BaselinePeriodDays = stored.BaselinePeriodDays,
            GeneratedAt = new DateTimeOffset(DateTime.SpecifyKind(stored.GeneratedAtUtc, DateTimeKind.Utc)),
        };
    }

    /// <summary>
    /// "No trajectory yet" — a member under a month of readings, or one whose narrative has gone
    /// stale. Empty text rather than an error: there is nothing wrong, there is just nothing this
    /// can honestly say.
    /// </summary>
    private static TrendInsightResponse NoTrend(Guid cardiMemberId) => new()
    {
        CardiMemberId = cardiMemberId,
        Narrative = string.Empty,
        KeyFindings = [],
        GeneratedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>
    /// Whether this member is actually being watched right now — active, and not paused.
    /// </summary>
    /// <remarks>
    /// Deliberately not applied to the alert explanation. An alert raised before a pause is still
    /// listed and still opens, and hiding only the reason it fired would leave a caregiver looking
    /// at an alert the product refuses to explain. The two member-scoped reads are different: they
    /// describe how someone is doing *now*, which is precisely what a pause says is no longer
    /// being observed.
    /// </remarks>
    private async Task<bool> IsBeingWatchedAsync(Guid cardiMemberId)
    {
        var member = await _unitOfWork.CardiMembers.GetByIdAsync(cardiMemberId);
        return member is not null && member.IsActive && !member.IsMonitoringPaused(DateTime.UtcNow);
    }

    /// <summary>
    /// An instant as UTC, whatever kind it arrived as — rows read back from Postgres come through
    /// unspecified, and a fixture may hand this a local one.
    /// </summary>
    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Local => value.ToUniversalTime(),
        DateTimeKind.Utc => value,
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

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

        // Shared with chat's status rung rather than judged here: the header this line renders in
        // and the chat answer beneath it must not disagree about whether there is a current line.
        var line = await _unitOfWork.MemberStatusLines.GetByCardiMemberAsync(cardiMemberId);
        if (!StatusLineServability.IsServable(line, DateTime.UtcNow))
            return NoStatusMessage();

        return new CurrentStatusMessageResponse
        {
            Headline = line.Headline,
            Message = line.Message,
            GeneratedAt = new DateTimeOffset(DateTime.SpecifyKind(line.GeneratedAtUtc, DateTimeKind.Utc)),
        };
    }

    /// <summary>
    /// "Nothing to suggest yet" — the empty counterpart to <see cref="NoStatusMessage"/>.
    /// </summary>
    private static AdviseResponse NoAdvise(Guid cardiMemberId) => new()
    {
        CardiMemberId = cardiMemberId,
        Summary = string.Empty,
        Suggestion = string.Empty,
        GuidelineCited = null,
        GeneratedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>
    /// Read-only, the same shape as <see cref="GetCurrentStatusMessageAsync"/>: the suggestion is
    /// generated by the pipeline's batch pass (<c>AdviseGenerationService</c>) and persisted per
    /// member; this serves the latest row. No model call on this path, which is what lets a
    /// caregiver open CardiMember Details — and the Dashboard read the presence of a row for its
    /// pulse indicator — without ever waking MedGemma.
    /// </summary>
    public async Task<AdviseResponse> GetAdviseAsync(
        Guid requestingUserId, Guid cardiMemberId, CancellationToken ct = default)
    {
        await _access.RequireViewAccessAsync(requestingUserId, cardiMemberId, ct);

        // Same guard GetCurrentStatusMessageAsync applies: a paused or deactivated member's stored
        // suggestion describes a monitoring state that no longer exists.
        var member = await _unitOfWork.CardiMembers.GetByIdAsync(cardiMemberId);
        if (member is null || !member.IsActive || member.IsMonitoringPaused(DateTime.UtcNow))
            return NoAdvise(cardiMemberId);

        // Shared with the Dashboard's pulse indicator and member chat's advice reply, so the three
        // cannot answer differently for the same rows — AdvisePicker applies AdviseServability and
        // the same fallback order everywhere.
        var advise = AdvisePicker.PickDefault(
            await _unitOfWork.MemberAdvises.GetAllByCardiMemberAsync(cardiMemberId), DateTime.UtcNow);
        if (advise is null)
            return NoAdvise(cardiMemberId);

        return new AdviseResponse
        {
            CardiMemberId = cardiMemberId,
            Summary = advise.Summary,
            Suggestion = advise.Suggestion,
            GuidelineCited = advise.GuidelineCited,
            GeneratedAt = new DateTimeOffset(DateTime.SpecifyKind(advise.GeneratedAtUtc, DateTimeKind.Utc)),
        };
    }

    /// <summary>
    /// How many entries one read returns. Generous for a year of a log that only gains a line
    /// when something changes, and a ceiling rather than a page size: this is a record somebody
    /// reads end to end before an appointment, so paging it would be a worse answer than a
    /// bounded one that says it was bounded.
    /// </summary>
    internal const int ObservationLogLimit = 200;

    /// <inheritdoc/>
    public async Task<AdviseObservationLogResponse> GetAdviseObservationsAsync(
        Guid requestingUserId,
        Guid cardiMemberId,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        CancellationToken ct = default)
    {
        await _access.RequireViewAccessAsync(requestingUserId, cardiMemberId, ct);

        var utcNow = DateTime.UtcNow;
        var earliest = AdviseObservationRetention.CutoffAt(utcNow);

        // Deliberately no pause or IsActive guard, unlike GetAdviseAsync. That one withholds a
        // *current* suggestion because it describes a monitoring state that has since stopped
        // existing. This is a dated record: what was noticed last March was noticed last March,
        // and a pause today does not make it untrue. Access is still checked, which is the part
        // that protects the member.
        var toUtc = (to?.UtcDateTime ?? utcNow) is var requestedTo && requestedTo > utcNow
            ? utcNow
            : requestedTo;
        var fromUtc = (from?.UtcDateTime ?? earliest) is var requestedFrom && requestedFrom < earliest
            ? earliest
            : requestedFrom;

        // An inverted or empty window is answered with an empty log rather than an error: the
        // caller asked what happened between two instants, and "nothing" is the true answer for a
        // window containing no time.
        if (fromUtc >= toUtc)
        {
            return new AdviseObservationLogResponse
            {
                CardiMemberId = cardiMemberId,
                From = Utc(fromUtc),
                To = Utc(toUtc),
                Observations = [],
                Truncated = false,
            };
        }

        // One over the limit, so "there were more" is answered by the read itself rather than by a
        // second count query over the same rows.
        var rows = await _unitOfWork.MemberAdviseObservations.GetByCardiMemberAsync(
            cardiMemberId, fromUtc, toUtc, ObservationLogLimit + 1, ct);

        var truncated = rows.Count > ObservationLogLimit;

        return new AdviseObservationLogResponse
        {
            CardiMemberId = cardiMemberId,
            From = Utc(fromUtc),
            To = Utc(toUtc),
            Observations =
            [
                .. rows.Take(ObservationLogLimit).Select(o => new AdviseObservationResponse
                {
                    Topic = o.Topic,
                    Summary = o.Summary,
                    Suggestion = o.Suggestion,
                    GuidelineCited = o.GuidelineCited,
                    ObservedAt = Utc(o.ObservedAtUtc),
                })
            ],
            Truncated = truncated,
        };
    }

    /// <summary>
    /// Stamps a stored instant as UTC before it leaves as a <see cref="DateTimeOffset"/>. Npgsql
    /// hands these back as <see cref="DateTimeKind.Utc"/>, but a row built in memory by a test or
    /// a seed carries Unspecified, and the conversion reads that as local — which is how a log
    /// entry lands on a caregiver's screen an hour out.
    /// </summary>
    private static DateTimeOffset Utc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static string BuildAlertPrompt(
        Alert alert,
        string memberContext,
        IEnumerable<ActivityLog> recentLogs,
        PatternBaseline? baseline,
        DateOnly today)
    {
        var recentSummary = MedicalPromptBlocks.JsonFence(
            MedicalPromptBlocks.DailyReadingsJson(recentLogs, take: 3, today));

        return $"""
            {AlertInstructions}

            [PATIENT CONTEXT]
            {memberContext}

            --- Alert ---
            Type: {alert.AlertType}
            Severity: {alert.Severity}
            Title: {AlertField(alert.Title)}
            Message: {AlertField(alert.Message)}
            Triggered: {alert.TriggeredDate:yyyy-MM-dd HH:mm} UTC
            Metric values: {AlertFieldOrNone(alert.MetricValues)}

            Known baselines: {MedicalPromptBlocks.BaselineSummary(baseline)}

            [INPUT DATA]
            {recentSummary}
            """;
    }

    /// <summary>
    /// One of the alert's own fields, reduced to the single line its <c>Key: value</c> row assumes
    /// and bounded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These reached the prompt raw, while the same three fields go through
    /// <c>MedicalPromptBlocks.Flatten</c> in every other renderer that carries an alert — the
    /// digest's monitoring context and the Daybook's. A newline in one of them puts the rest of
    /// the field on an unlabelled top-level line inside a section, which is the shape the
    /// flattening exists to make impossible.
    /// </para>
    /// <para>
    /// <c>MetricValues</c> is the one that matters most: it is a serialised blob rather than a
    /// sentence, so it is both the longest of the three and the least useful to the model per
    /// character it costs.
    /// </para>
    /// </remarks>
    private static string AlertField(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : MedicalPromptBlocks.CutTo(MedicalPromptBlocks.Flatten(value), MaxAlertFieldLength);

    /// <summary>Ceiling on one alert field — enough for the sentence, not for a payload.</summary>
    private const int MaxAlertFieldLength = 300;

    /// <summary><see cref="AlertField"/>, saying so plainly when the field is empty.</summary>
    private static string AlertFieldOrNone(string? value) =>
        AlertField(value) is { Length: > 0 } text ? text : "none";

    /// <summary>
    /// The established-baseline prompt: their learned normal, and which parts of it this week has
    /// departed from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The seven-day JSON fence of every reading is deliberately gone. Handed a table of readings
    /// and asked to describe it, the model described it — a line per metric, in which "resting
    /// heart rate remained relatively stable" was reported as a key finding. Which metrics have
    /// actually departed is <see cref="BaselineMovementCalculator"/>'s judgement now, made in .NET
    /// where it can be reproduced, so what arrives here is already the answer to "is anything
    /// off" and the model's job is to say it in a family's words.
    /// </para>
    /// <para>
    /// The baselines and the sleep window stay. They are what the departures are measured from
    /// rather than a second table to narrate, and the window is the one piece of the picture the
    /// movements cannot carry — it is a time of day, not a figure that moved. It keeps the local
    /// clock it was given: a household's bedtime read off Greenwich is the failure
    /// <see cref="MemberAnchorTimeZone"/> exists to prevent.
    /// </para>
    /// </remarks>
    private static string BuildBaselinePrompt(
        string memberContext,
        IEnumerable<PatternBaseline> baselines,
        BaselineMovements movements,
        DateOnly today,
        TimeZoneInfo? timeZone)
    {
        var baselineLines = baselines.Select(b =>
            MedicalPromptBlocks.BaselineSummary(b) + SleepWindow(b, today, timeZone));

        return $"""
            {BaselineInstructions}

            [PATIENT CONTEXT]
            {memberContext}

            --- Baselines ---
            {string.Join("\n", baselineLines)}

            --- What has moved this week ---
            {BaselineMovementCalculator.Render(movements)}
            """;
    }

    private static string BuildProvisionalPrompt(
        string memberContext,
        PatternBaseline baseline,
        IEnumerable<ActivityLog> recentLogs,
        DateOnly today,
        TimeZoneInfo? timeZone)
    {
        return $"""
            {ProvisionalInstructions}

            [PATIENT CONTEXT]
            {memberContext}

            --- Provisional baseline ---
            {MedicalPromptBlocks.BaselineSummary(baseline, provisional: true)}{SleepWindow(baseline, today, timeZone)}

            [INPUT DATA]
            {MedicalPromptBlocks.JsonFence(MedicalPromptBlocks.DailyReadingsJson(recentLogs, take: 7, today))}
            """;
    }

    private static string BuildLearningPrompt(
        string memberContext, IReadOnlyCollection<ActivityLog> recentLogs, DateOnly today)
    {
        var daysObserved = recentLogs.Select(l => l.Date).Distinct().Count();

        return $"""
            {LearningInstructions}

            [PATIENT CONTEXT]
            {memberContext}

            --- Observation so far ---
            Days with data in the last 14: {daysObserved}
            No baseline has been established yet.

            [INPUT DATA]
            {MedicalPromptBlocks.JsonFence(MedicalPromptBlocks.DailyReadingsJson(recentLogs, take: 14, today))}
            """;
    }

    // Member context, note flattening, and JSON daily readings live in MedicalPromptBlocks,
    // shared with the digest pipeline so the minimisation and injection-framing rules cannot
    // drift between the private model's callers.

    /// <summary>
    /// Typical bedtime and wake time, when the baseline has settled on them — put on the member's
    /// own local clock, and said so.
    /// </summary>
    /// <remarks>
    /// Stored in UTC and formerly handed over labelled that way, which was honest but left the
    /// model reasoning about a household's evening on Greenwich's clock. Converting is the better
    /// half of that trade now that a zone is resolvable here: the label stays, because an
    /// unlabelled "22:40" is the failure either way. Anchored to <paramref name="today"/> so a
    /// baseline read either side of a daylight-saving change lands on the clock the household
    /// actually keeps.
    /// </remarks>
    private static string SleepWindow(PatternBaseline baseline, DateOnly today, TimeZoneInfo? timeZone) =>
        BaselineClock.Local(baseline.TypicalBedtime, today, timeZone) is { } bedtime
        && BaselineClock.Local(baseline.TypicalWakeTime, today, timeZone) is { } wake
            ? $", Typical sleep window: {bedtime:HH\\:mm}–{wake:HH\\:mm} local"
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
