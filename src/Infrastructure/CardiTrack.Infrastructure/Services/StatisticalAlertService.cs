using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using CardiTrack.Application.Interfaces.Clients;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Domain.Extensions;
using CardiTrack.Infrastructure.Diagnostics;
using CardiTrack.Infrastructure.Services.PromptContext;
using Microsoft.Extensions.Logging;

namespace CardiTrack.Infrastructure.Services;

/// <summary>
/// The R1 statistical pass (docs/execution/backend/api/alerts.md): each recently-active
/// member's daily readings are evaluated against their established 30-day baseline by the pure
/// rules in <see cref="StatisticalAlertRules"/>, and every finding that survives cooldown and
/// dedup is handed to the private medical model for its verdict — severity, headline and the
/// sentences a caregiver reads. The rules are an input provider; the inference is MedGemma's.
/// Fetching the 30-day baseline and nothing else is how "provisional baselines never alert" is
/// enforced for the <b>comparative</b> rules — those asking whether a reading is unusual for this
/// member stay silent without one.
/// <para>
/// The <b>measured</b> rules (<c>irregular_rhythm</c>, <c>ecg_afib</c>) sit deliberately outside
/// that gate. They carry a finding the wearer's own device made and classified, so there is no
/// inference in them for a thin window to weaken. Skipping a member without a baseline would mean
/// someone two weeks into wearing a watch hears nothing when it tells them their heart is in
/// atrial fibrillation — the one silence this engine must never produce. Their severity and
/// wording still come from the model, exactly like every other finding.
/// </para>
/// <para>
/// Until 2026-09-19 this ran in <c>CardiTrack.Worker</c> and wrote each rule's own hard-coded
/// severity and copy straight into the alert row: a threshold constant paged families with no
/// model in the loop, the dashboard tier took its colour from that constant, and the chat
/// verdict was then held to the colour. It now runs in the pipeline's <c>assess</c> job, after
/// the real-time assessor and before the digest pass, because a job that calls MedGemma is AI
/// pipeline work per CLAUDE.md and cannot live in the Worker.
/// </para>
/// <para>
/// Two layers keep a five-minute cadence from paging anyone twice, and both run <em>before</em>
/// the model is consulted so a finding that would be suppressed costs no inference: the
/// rule-scoped cooldown (<see cref="AlertRuleMarkers.Suppresses"/> — one unresolved
/// <em>standing</em> alert per remedy) and a same-local-day dedup (a daily-grain rule that
/// already judged today — whether that alert is still on the list, resolved, or the caregiver
/// deleted it — must not re-fire from the same day's data that evening). A finding the model
/// judged benign is not written anywhere, so it is judged again on the next pass while its
/// yardstick keeps tripping — up to one call per pass for that member until the readings move
/// or the day turns — and the rules only produce a finding when a yardstick is crossed, so a
/// member with nothing off costs nothing. Persisting a benign verdict as a judged-day marker
/// would bound that to one call per rule-day; it needs a row of its own (a green alert would
/// re-surface the retired benign-sleep card), and is left as the follow-up it is.
/// </para>
/// <para>
/// <b>Fail closed.</b> A model call that throws, a verdict the parser cannot map, a verdict for
/// a rule this pass did not ask about, or copy a register guard rejects all produce no alert —
/// logged, counted, and left for the next pass to re-judge. Code never supplies a severity or a
/// sentence of its own in the model's place: that would put the constant back in the loop.
/// </para>
/// </summary>
public class StatisticalAlertService : IStatisticalAlertService
{
    /// <summary>
    /// Stored as the alert's message when the model's sentences name a condition. Severity still
    /// routes; the sentence a caregiver reads must not carry a diagnosis. Same stance as
    /// <c>RealtimeAssessmentService.NonClinicalObservation</c>.
    /// </summary>
    internal const string NonClinicalObservation =
        "A reading sat far enough from this person's usual pattern to be worth a look.";

    /// <summary>
    /// <c>CARDITRACK_STATISTICAL_JUDGEMENT_PROMPT</c> — the daily findings judgement
    /// (docs/llm_design.md prompt registry). The register is
    /// <see cref="MedicalPromptBlocks.CaregiverRegister"/>. No sample copy: MedGemma echoes it.
    /// The yardsticks travel in each finding because they are what made the reading worth
    /// judging, not what the verdict must be — the brief says so in as many words. Fixed prefix;
    /// member data always goes after it.
    /// </summary>
    /// <remarks>
    /// Internal rather than private so <see cref="MedicalPromptToneTests"/>' reflection covers it
    /// with every other prompt on the platform.
    /// </remarks>
    internal const string JudgementInstructions =
        MedicalPromptBlocks.Tone + """
        Judge these findings from a family member's wearable readings for their caregiver.

        """ + MedicalPromptBlocks.CaregiverRegister + """
        Each finding names what was measured, what is usual for this person, and the yardstick that made the reading worth judging. A yardstick is a threshold, not a verdict: a reading past one may still be ordinary for this person on this day, and a reading that clears it narrowly is not the same as one far beyond it. Judge each finding against the person's own usual first and the published range where one is given, read the findings together where they describe the same day, and weigh what is known about the person before calling anything unusual.

        Respond with one verdict per finding, in the order given, each carrying the finding's rule exactly as written:
        - rule: the finding's rule, copied exactly.
        - severity: exactly one of critical, high, medium, or low, from most to least severe. Low means the finding is not worth the family's attention today and nothing is raised.
        - headline: two to six words naming what was seen, in sentence case, with no full stop, no name and no CardiTrackCardiMember.
        - message: 1-3 plain sentences the caregiver can act on. Name no day, no date and no clock time — the app dates the finding itself, and a "yesterday" written today is wrong by tomorrow.
        """ + MedicalPromptBlocks.ContextGuardrail;

    private readonly IUnitOfWork _unitOfWork;
    private readonly IMedicalAiService _medicalAi;
    private readonly MemberContextComposer _memberContext;
    private readonly StatusLineGenerationService _statusLine;
    private readonly ILogger<StatisticalAlertService> _logger;
    private readonly IAlertNotificationEnqueue? _alertEnqueue;
    private readonly IMemberWriteGuard _guard;

    /// <summary>
    /// Optional so the many tests that exercise the judgement path need not stand one up, and so
    /// a host that has not registered the insight service still raises alerts. A missing
    /// explanation costs the detail screen one card; a missing alert costs a caregiver the thing
    /// they bought the product for.
    /// </summary>
    private readonly IHealthInsightService? _insights;

    public StatisticalAlertService(
        IUnitOfWork unitOfWork,
        IMedicalAiService medicalAi,
        MemberContextComposer memberContext,
        StatusLineGenerationService statusLine,
        ILogger<StatisticalAlertService> logger,
        IMemberWriteGuard guard,
        IAlertNotificationEnqueue? alertEnqueue = null,
        IHealthInsightService? insights = null)
    {
        _unitOfWork = unitOfWork;
        _medicalAi = medicalAi;
        _memberContext = memberContext;
        _statusLine = statusLine;
        _logger = logger;
        _guard = guard;
        _alertEnqueue = alertEnqueue;
        _insights = insights;
    }

    public async Task<int> EvaluateAsync(DateTime utcNow, CancellationToken ct = default)
    {
        // The established candidate filter: active members with data in the last two days.
        var since = DateOnly.FromDateTime(utcNow).AddDays(-2);
        var memberIds = (await _unitOfWork.CardiMembers.GetActiveIdsWithActivitySinceAsync(since)).ToList();

        var raised = 0;
        foreach (var memberId in memberIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                raised += await EvaluateMemberAsync(memberId, utcNow, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Shutdown, not a member failure.
                throw;
            }
            catch (Exception ex)
            {
                // One member's failure — a model hiccup included — must not cost the rest of the
                // fleet this pass; the next scheduled run re-judges the same findings.
                _logger.LogError(ex, "Statistical judgement failed for CardiMember {CardiMemberId}.", memberId);
            }
        }

        await BackfillPassAsync(utcNow, ct);

        _logger.LogInformation(
            "Statistical judgement pass complete. Members evaluated: {MembersEvaluated}, alerts raised: {Raised}.",
            memberIds.Count, raised);
        return raised;
    }

    /// <summary>
    /// The explanation sweep, over everyone with an alert a caregiver could still open.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Its own pass rather than a step inside the member loop, because the two candidate sets are
    /// not the same one. The rules are driven by members with readings in the last two days, which
    /// is right for judging today's data and wrong for this: an alert goes on being readable long
    /// after the readings stop, and <c>device_silence</c> stays unresolved precisely
    /// <em>because</em> they have stopped. Riding the rule pass's filter meant the member whose
    /// watch had been quiet for three days — the one most likely to be holding an unexplained
    /// alert — was the first one the sweep could no longer see.
    /// </para>
    /// <para>
    /// Every member here is re-checked for being active and unpaused, the same gate the rule pass
    /// applies: an explanation is a thing said about someone being watched, and monitoring being
    /// paused is them asking us to stop.
    /// </para>
    /// </remarks>
    private async Task BackfillPassAsync(DateTime utcNow, CancellationToken ct)
    {
        if (_insights is null)
            return;

        var memberIds = await _unitOfWork.Alerts.GetCardiMemberIdsWithServableAlertsAsync(
            utcNow - ExplanationBackfillWindow);

        foreach (var memberId in memberIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var member = await _unitOfWork.CardiMembers.GetByIdAsync(memberId);
                if (member is null || !member.IsActive || member.IsMonitoringPaused(utcNow))
                    continue;

                await BackfillExplanationsAsync(memberId, utcNow, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One member's failure must not cost the rest the sweep, the same stance the rule
                // loop above takes.
                _logger.LogError(
                    ex, "Explanation backfill failed for CardiMember {CardiMemberId}.", memberId);
            }
        }
    }

    private async Task<int> EvaluateMemberAsync(Guid memberId, DateTime utcNow, CancellationToken ct)
    {
        // One span per member per pass, under the job's root span. Everything from here down used
        // to be unobservable: three ways to leave on the next line alone, none of them logged, and
        // a member skipped for a paused monitor looked exactly like a member with nothing off.
        // See JudgementTelemetry for why the member's id is not a tag on it.
        using var activity = JudgementTelemetry.Source.StartActivity(
            "judgement.member", ActivityKind.Internal);

        var member = await _unitOfWork.CardiMembers.GetByIdAsync(memberId);
        if (member is null || !member.IsActive || member.IsMonitoringPaused(utcNow))
            return 0;

        // Established baseline only for the COMPARATIVE rules: without a 30-day baseline every
        // rule that asks "is this unusual for them" stays silent, exactly as the
        // provisional-never-alerts principle demands.
        //
        // The MEASURED rules are not gated on it, and this is the whole reason the two kinds are
        // named apart. A measured rule reports a finding the wearer's own device made — an ECG it
        // classified, a rhythm notification it raised — so there is no inference in it to be thin,
        // and nothing for a baseline to make surer. Returning early here would mean a member two
        // weeks into wearing a watch gets no word when it tells them their heart is in atrial
        // fibrillation, which is the one silence this engine must never produce.
        var baseline = await _unitOfWork.PatternBaselines.GetLatestByCardiMemberAsync(memberId, periodDays: 30);

        var rulePrefs = AlertRuleOverrides.FromJson(
            (await _unitOfWork.AlertPreferences.GetByCardiMemberIdAsync(memberId, ct))?.DisabledRules);

        // Prefer skipping timezone + activity-log fetches when every statistical rule is off.
        if (!rulePrefs.IsEnabled(StatisticalAlertRules.ActivityDeclineRule)
            && !rulePrefs.IsEnabled(StatisticalAlertRules.IrregularSleepRule)
            && !rulePrefs.IsEnabled(StatisticalAlertRules.ElevatedHeartRateRule)
            && !rulePrefs.IsEnabled(StatisticalAlertRules.NoMorningActivityRule)
            && !rulePrefs.IsEnabled(StatisticalAlertRules.LongTermTrendRule)
            && !rulePrefs.IsEnabled(StatisticalAlertRules.HeartRateVariabilityDropRule)
            && !rulePrefs.IsEnabled(StatisticalAlertRules.OvernightBreathingUpRule)
            && !rulePrefs.IsEnabled(StatisticalAlertRules.ElevatedZoneWithoutMovementRule)
            && !rulePrefs.IsEnabled(StatisticalAlertRules.DaytimeInactivityBlockRule)
            && !rulePrefs.IsEnabled(StatisticalAlertRules.IrregularRhythmRule)
            && !rulePrefs.IsEnabled(StatisticalAlertRules.EcgAtrialFibrillationRule))
        {
            return 0;
        }

        // Every comparative rule is off, or there is no baseline for them to compare against, and
        // both measured rules are off too — nothing below can produce a finding, so skip the
        // timezone and activity-log fetches.
        if (baseline is null
            && !rulePrefs.IsEnabled(StatisticalAlertRules.IrregularRhythmRule)
            && !rulePrefs.IsEnabled(StatisticalAlertRules.EcgAtrialFibrillationRule))
        {
            return 0;
        }

        var timeZone = await MemberAnchorTimeZone.ResolveAsync(_unitOfWork, memberId);
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(utcNow, timeZone);
        var localToday = DateOnly.FromDateTime(localNow);
        var yesterday = localToday.AddDays(-1);

        // One fetch covers every rule: yesterday and today for the daily rules, four trailing
        // weeks for the trend. Stored dates are the wearer's civil days.
        //
        // Narrowed to two days when there is no baseline, because only the measured rules can run
        // and both read today and yesterday. That keeps most of the "no wasted reads" property the
        // provisional-never-alerts gate used to give for free: a member in their first 30 days
        // costs one two-day indexed range read per pass rather than a 28-day one, and gets told
        // when their watch finds atrial fibrillation.
        var windowStart = baseline is null
            ? yesterday
            : localToday.AddDays(-7 * StatisticalAlertRules.TrendWeeks);

        var logsByDate = (await _unitOfWork.ActivityLogs.GetByCardiMemberAndDateRangeAsync(
                memberId, windowStart, localToday))
            .ToDictionary(l => l.Date);
        var yesterdayLog = logsByDate.GetValueOrDefault(yesterday);
        var todayLog = logsByDate.GetValueOrDefault(localToday);

        // Sleep sessions are attributed to the civil day they ENDED on, so last night lives on
        // today's log, not yesterday's — the dashboard's sleep card rates the same row, and the
        // two surfaces must not disagree about which night "last night" is. Yesterday's log is
        // the fallback for a night whose data only arrived after local midnight; the per-night
        // dedup below is what keeps that fallback from re-judging a night that already alerted.
        var lastNightLog = todayLog?.SleepMinutes is not null ? todayLog : yesterdayLog;

        // Same attribution, judged on its own presence rather than on sleep's. HRV and overnight
        // breathing are measured across the night like sleep is, but they do not always arrive with
        // it: a device can post the night's vitals while its sleep session is still syncing, and
        // gating on SleepMinutes would send these two rules a day back to a night they have already
        // judged. Whichever reading is there decides which row is "last night" for that reading.
        var overnightVitalsLog =
            todayLog?.HeartRateVariabilityMs is not null
            || todayLog?.OvernightBreathingRate is not null
            || todayLog?.SleepMinutes is not null
                ? todayLog
                : yesterdayLog;

        // The night before last night, for the HRV rule — one low night is noise, two is a signal.
        // Sleep-derived readings follow the same "ended on this day" attribution as sleep itself,
        // so the night before last night lives on the row before last night's.
        var previousNightLog = logsByDate.GetValueOrDefault(
            overnightVitalsLog?.Date.AddDays(-1) ?? yesterday);

        // Off = do not evaluate at all (not merely suppress the raise). Absence of a preference
        // row means every rule is on.
        var findings = new List<StatisticalFinding>();

        // Measured rules first, and outside the baseline guard below: these report what the
        // device itself found, not what is unusual for this member.
        if (rulePrefs.IsEnabled(StatisticalAlertRules.IrregularRhythmRule))
            AddIfPresent(findings, StatisticalAlertRules.IrregularRhythm(todayLog, yesterdayLog));
        if (rulePrefs.IsEnabled(StatisticalAlertRules.EcgAtrialFibrillationRule))
            AddIfPresent(findings, StatisticalAlertRules.EcgAtrialFibrillation(todayLog, yesterdayLog));

        if (baseline is not null)
        {
            if (rulePrefs.IsEnabled(StatisticalAlertRules.ActivityDeclineRule))
                AddIfPresent(findings, StatisticalAlertRules.ActivityDecline(baseline, yesterdayLog));
            if (rulePrefs.IsEnabled(StatisticalAlertRules.IrregularSleepRule))
            {
                // Age against the member's own local today, the same day the readings are dated in —
                // the sleep rule grades the night on the published band for their age bracket.
                AddIfPresent(findings, StatisticalAlertRules.IrregularSleep(
                    baseline, lastNightLog, member.DateOfBirth.ToAgeInYears(localToday)));
            }
            if (rulePrefs.IsEnabled(StatisticalAlertRules.ElevatedHeartRateRule))
                AddIfPresent(findings, StatisticalAlertRules.ElevatedHeartRate(baseline, yesterdayLog));
            if (rulePrefs.IsEnabled(StatisticalAlertRules.NoMorningActivityRule))
                AddIfPresent(findings, StatisticalAlertRules.NoMorningActivity(baseline, todayLog, localNow));
            if (rulePrefs.IsEnabled(StatisticalAlertRules.LongTermTrendRule))
                AddIfPresent(findings, StatisticalAlertRules.LongTermTrend(logsByDate, yesterday));
            if (rulePrefs.IsEnabled(StatisticalAlertRules.HeartRateVariabilityDropRule))
            {
                AddIfPresent(findings, StatisticalAlertRules.HeartRateVariabilityDrop(
                    baseline, overnightVitalsLog, previousNightLog));
            }
            if (rulePrefs.IsEnabled(StatisticalAlertRules.OvernightBreathingUpRule))
            {
                // Last night's row, like sleep and HRV: the reading is derived from the night and is
                // filed under the civil day it ended on.
                AddIfPresent(findings, StatisticalAlertRules.OvernightBreathingUp(baseline, overnightVitalsLog));
            }
            if (rulePrefs.IsEnabled(StatisticalAlertRules.ElevatedZoneWithoutMovementRule))
                AddIfPresent(findings, StatisticalAlertRules.ElevatedZoneWithoutMovement(baseline, yesterdayLog));
            if (rulePrefs.IsEnabled(StatisticalAlertRules.DaytimeInactivityBlockRule))
                AddIfPresent(findings, StatisticalAlertRules.DaytimeInactivityBlock(baseline, yesterdayLog));
        }

        // NOTE: this pass's alerts are not auto-resolved, and so still latch — see
        // AlertResolution for what that costs. Closing them needs each rule to say whether it was
        // able to judge at all: every rule here returns null both when it did not trip and when
        // its inputs were missing (no reading for yesterday, too few days for the trend), and
        // treating the second as "the episode has passed" would resolve a standing alert on a day
        // that produced no evidence either way. On a health screen that is the wrong failure —
        // better a rule that stays latched than one that quietly stands down in the dark.
        activity?.SetTag(JudgementTelemetry.FindingsTag, findings.Count);
        if (findings.Count == 0)
            return 0;

        activity?.SetTag(
            JudgementTelemetry.RulesTag, string.Join(',', findings.Select(f => f.Rule)));

        // Soft-deleted rows are part of the history a daily rule already judged. Fetching only
        // standing alerts meant deleting a card re-armed the same quieter day on the next tick
        // — the caregiver's housekeeping became a new page. Cooldown still looks at standing
        // rows only: this pass does not auto-resolve, so a deleted alert must not latch the
        // rule forever. Same-data dedup below reads the full history.
        var history = (await _unitOfWork.Alerts.GetByCardiMemberAsync(memberId, activeOnly: false)).ToList();
        var standing = history.Where(a => a.IsActive).ToList();

        bool FiredOnLocalToday(Alert a) =>
            DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(a.TriggeredDate, timeZone)) == localToday;

        var toJudge = new List<StatisticalFinding>();
        foreach (var finding in findings)
        {
            if (standing.Any(a => AlertRuleMarkers.Suppresses(a, finding.Type, finding.Rule)))
                continue;

            // Same-data dedup, regardless of resolution or deletion: a rule reads one day's data,
            // so one day's data gets at most one alert from it — resolving or deleting at noon
            // must not re-page at half past from the same readings. A finding that names the
            // night it judged dedups on that night rather than the firing day, because
            // late-arriving data can put the same night in front of the rule on two calendar
            // days; an alert from before night markers existed cannot say which night it judged
            // and is read as today's.
            var judgedAlready = finding.NightOf is { } night
                ? history.Any(a => AlertRuleMarkers.HasRule(a, finding.Rule)
                    && (AlertRuleMarkers.HasNight(a, night)
                        || (!AlertRuleMarkers.HasAnyNight(a) && FiredOnLocalToday(a))))
                : history.Any(a => AlertRuleMarkers.HasRule(a, finding.Rule) && FiredOnLocalToday(a));
            if (judgedAlready)
                continue;

            toJudge.Add(finding);
        }

        activity?.SetTag(JudgementTelemetry.JudgedTag, toJudge.Count);
        if (toJudge.Count == 0)
            return 0;

        // One call per member per pass, however many findings: the model reads them together,
        // which is the point — a quiet day and a raised overnight vital are one picture, not two.
        // The member's own local day, not the UTC one: the context block dates and ages the
        // person by it, and the findings above are already dated in that calendar.
        var memberContext = await _memberContext.ComposeAsync(
            new MemberContextRequest(member, memberId, localToday, utcNow, PromptPurpose.StatisticalJudgement),
            ct);
        var prompt = BuildPrompt(JudgementInstructions, memberContext, toJudge);

        ct.ThrowIfCancellationRequested();
        var response = await _medicalAi.GenerateStructuredAsync<JudgementAiResponse>(prompt, ct);

        var voice = MemberVoice.For(member);
        var created = new List<Alert>();
        foreach (var finding in toJudge)
        {
            // Matched by rule, never by position: a model that drops or reorders a verdict must
            // not have its answer about one finding written against another.
            var verdict = response.Verdicts?.FirstOrDefault(v =>
                string.Equals(v.Rule?.Trim(), finding.Rule, StringComparison.OrdinalIgnoreCase));
            if (verdict is null)
            {
                CountVerdict(JudgementTelemetry.OutcomeUnmatched, finding.Rule);
                _logger.LogWarning(
                    "The model returned no verdict for rule {Rule} on CardiMember {CardiMemberId}; nothing raised.",
                    finding.Rule, memberId);
                continue;
            }

            var (rawSeverity, severity) = AssessmentSeverityParser.Map(verdict.Severity);
            if (severity is null)
            {
                // Fail closed: a severity word outside the taxonomy is no verdict. The model
                // cannot page a family by deviating from the schema's vocabulary.
                CountVerdict(JudgementTelemetry.OutcomeSeverityUnmapped, finding.Rule);
                _logger.LogWarning(
                    "The model's severity {RawSeverity} for rule {Rule} on CardiMember {CardiMemberId} did not map; nothing raised.",
                    rawSeverity, finding.Rule, memberId);
                continue;
            }

            if (severity < AlertSeverity.Yellow)
            {
                // Judged benign. Nothing is written — there is no benign alert to show — and the
                // same-day dedup does not see it, so the finding is judged again next pass with
                // whatever the day has added. That is deliberate: a verdict is about the readings
                // as they stand, and the readings keep arriving.
                //
                // Counted like the others, and it is the denominator that matters: in prod the
                // root log level is Warning, so this line does not exist there at all and the
                // failures had nothing to be a proportion of.
                CountVerdict(JudgementTelemetry.OutcomeBenign, finding.Rule);
                _logger.LogInformation(
                    "The model judged rule {Rule} on CardiMember {CardiMemberId} not worth attention today.",
                    finding.Rule, memberId);
                continue;
            }

            var message = CaregiverFacingMessage(verdict.Message, voice);
            if (message is null)
            {
                CountVerdict(JudgementTelemetry.OutcomeMessageRejected, finding.Rule);
                _logger.LogWarning(
                    "The model's message for rule {Rule} on CardiMember {CardiMemberId} was unusable; nothing raised.",
                    finding.Rule, memberId);
                continue;
            }

            ct.ThrowIfCancellationRequested();
            var alert = new Alert
            {
                CardiMemberId = memberId,
                AlertType = finding.Type,
                Severity = severity.Value,
                Title = CaregiverFacingHeadline(verdict.Headline, finding.Rule),
                Message = message,
                TriggeredDate = utcNow,
                MetricValues = finding.MetricValues,
            };
            await _unitOfWork.Alerts.AddAsync(alert);
            created.Add(alert);
            CountVerdict(JudgementTelemetry.OutcomeRaised, finding.Rule);
        }

        activity?.SetTag(JudgementTelemetry.RaisedTag, created.Count);
        if (created.Count == 0)
            return 0;

        // Guarded like every other post-inference write: the judgement above is a MedGemma call
        // that can run for minutes, and an alert raised for an erased member is both health data
        // we may not hold and a page a family would receive about someone the product has
        // forgotten. Refused means nothing was written, so this pass raised nothing and every
        // step below — the status line, the explanations, the notification enqueue — is skipped.
        if (!await _guard.WriteIfMemberLivesAsync(memberId, _ => _unitOfWork.SaveChangesAsync(), ct))
            return 0;

        // A newly-raised alert moves the member's tier, and the persisted status line was
        // generated against whatever tier was current when the pipeline last wrote it. The model
        // is warm from the call above, so the line catches up in the same pass — the same
        // MS-7 resolution the assessor applies.
        await RegenerateStatusLineAsync(memberId, ct);

        // Transport, not a copy of the send stack: the API's internal enqueue endpoint runs the
        // same DispatchService the Worker uses, and DeliveryPlanner decides there what a yellow
        // versus an orange is owed. A failed POST must not roll back the alert, so this is
        // best-effort with a log; DispatchService's own dedup makes a retried call harmless.
        foreach (var alert in created)
        {
            if (_alertEnqueue is null)
            {
                _logger.LogWarning(
                    "Alert {AlertId} was raised with no enqueue transport registered — caregivers will not be pushed.",
                    alert.Id);
                break;
            }

            try
            {
                await _alertEnqueue.EnqueueForAlertAsync(alert.Id, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Push enqueue failed for Alert {AlertId}.", alert.Id);
            }
        }

        await ExplainAsync(created, ct);

        return created.Count;
    }

    /// <summary>
    /// Writes each new alert's caregiver-facing explanation, here rather than on the request path.
    /// This pass has already woken MedGemma to judge the findings, so the explanations ride a
    /// service that is warm; generating them when a caregiver taps the alert instead meant paying
    /// a cold start, or a 503, in front of someone who had just been told something was wrong.
    /// </summary>
    /// <remarks>
    /// Best-effort with a log, the same stance as the status line and the push enqueue above: the
    /// alerts are already stored, and an explanation that did not get written is a card the detail
    /// screen leaves out, not a reason to unwind the member's pass. Sequential rather than
    /// concurrent — these share the pass's DbContext, and EF Core refuses a second operation on a
    /// context while one is still running.
    /// </remarks>
    private async Task ExplainAsync(IReadOnlyList<Alert> created, CancellationToken ct)
    {
        if (_insights is null)
            return;

        foreach (var alert in created)
            await ExplainOneAsync(alert.Id, ct);
    }

    /// <summary>
    /// A bounded retry over the alerts a caregiver can still open, for explanations that never got
    /// written.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without it, an explanation lost to a model timeout or a guard rejection was lost for good:
    /// this pass is the only thing that writes one, it only ever sees <em>new</em> alerts, and the
    /// same finding on a later pass dedups against the alert already stored. The detail screen
    /// would then show that alert with no explanation for as long as it remained readable — which
    /// outlasts the episode, since resolving one does not hide it — and a later brief version
    /// would never reach it either.
    /// </para>
    /// <para>
    /// Cheap by construction: an alert already explained by the current brief costs one indexed
    /// lookup and no model call. The cap bounds the other case — an alert whose reply keeps
    /// failing the guards would otherwise be retried on every pass, of which there are 288 a day.
    /// </para>
    /// <para>
    /// The candidates are rotated rather than taken from the front, because the cap and a stable
    /// order together starve the tail. An alert the model will never produce a servable reply for
    /// — a guard rejection that repeats — stays unexplained, so it is a candidate on every pass,
    /// and taking the first two would spend the whole budget on the same two rows forever while a
    /// third candidate behind them was never once attempted. Rotating by the pass clock
    /// reaches every candidate within a few passes without persisting any retry state.
    /// </para>
    /// </remarks>
    private async Task BackfillExplanationsAsync(Guid memberId, DateTime utcNow, CancellationToken ct)
    {
        if (_insights is null)
            return;

        // Everything the detail screen will still serve, not just what is still unresolved.
        // AlertService.GetByIdAsync gates on IsActive alone, and AlertResolution sets IsResolved
        // without touching it — deliberately, so a closed episode stays readable in the archive.
        // Reading the unresolved set here meant a producer that resolved an alert before this
        // pass reached it (device silence re-arms the moment the watch reports again) left it
        // unexplained for good, on a card a caregiver can still open months later.
        var servable = await _unitOfWork.Alerts.GetServableByCardiMemberAsync(
            memberId, utcNow - ExplanationBackfillWindow);

        // Whether one needs a model call is decided here rather than read off the result, because
        // "already explained" and "spent a call and failed" both come back false. Counting
        // results would let an alert whose reply keeps failing the guards be retried on every
        // pass, which is the cost the cap exists to bound. The whole list is walked rather than
        // stopped at the cap — there is no rotating fairly over a set you have not counted — and
        // that is one lookup on a unique index per servable alert, which the window above bounds.
        var candidates = new List<Guid>();
        foreach (var alert in servable.OrderBy(a => a.CreatedDate).ThenBy(a => a.Id))
        {
            var stored = await _unitOfWork.MemberInsights.GetForAlertAsync(alert.Id);
            if (stored is null || stored.PromptVersion < HealthInsightService.AlertPromptVersion)
                candidates.Add(alert.Id);
        }

        if (candidates.Count == 0)
            return;

        var start = RotationOffset(utcNow, candidates.Count);
        var take = Math.Min(MaxExplanationRetriesPerPass, candidates.Count);
        for (var i = 0; i < take; i++)
            await ExplainOneAsync(candidates[(start + i) % candidates.Count], ct);
    }

    /// <summary>
    /// Where in the candidate list this pass starts, advancing one pass at a time so consecutive
    /// passes take consecutive slices. Derived from the clock rather than persisted: the pass
    /// cadence is fixed, so the slot number alone rotates, and a missed or repeated pass costs at
    /// most one candidate's turn.
    /// </summary>
    internal static int RotationOffset(DateTime utcNow, int candidateCount) =>
        candidateCount <= 0
            ? 0
            : (int)(Math.Abs(utcNow.Ticks / TimeSpan.TicksPerMinute / PassCadenceMinutes)
                    * MaxExplanationRetriesPerPass % candidateCount);

    /// <summary>How often the judgement pass runs, which is what the rotation advances by.</summary>
    private const int PassCadenceMinutes = 5;

    /// <summary>
    /// How far back a <em>resolved</em> alert stays a candidate for a missing explanation.
    /// Unresolved ones are candidates however old they are, as before — this only widens the set.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A fortnight is the retry horizon, not a statement about how long the alert matters: the
    /// explanation is served for the life of the alert (<see cref="InsightRetention"/> exempts
    /// these rows from the sweep for exactly that reason), but a reply that has failed every
    /// attempt for two weeks is failing for a reason another pass will not fix.
    /// </para>
    /// <para>
    /// The bound is what keeps the walk cheap. Every candidate costs one indexed lookup per pass
    /// and there are 288 passes a day, so an unbounded set would have this growing with the
    /// member's whole alert history forever. Live episodes plus a fortnight does not grow.
    /// </para>
    /// </remarks>
    private static readonly TimeSpan ExplanationBackfillWindow = TimeSpan.FromDays(14);

    /// <summary>
    /// How many alerts one pass will try to backfill an explanation for. Small on
    /// purpose: this runs every five minutes, and a member with a backlog catches up over a few
    /// passes rather than paying for all of it at once.
    /// </summary>
    private const int MaxExplanationRetriesPerPass = 2;

    /// <summary>
    /// One alert's explanation, best-effort: the alert is already stored, and an explanation that
    /// did not get written is a card the detail screen leaves out rather than a reason to unwind
    /// the member's pass.
    /// </summary>
    private async Task ExplainOneAsync(Guid alertId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            await _insights!.RegenerateAlertInsightAsync(alertId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex,
                "Insight generation failed for Alert {AlertId}; the alert was stored.",
                alertId);
        }
    }

    /// <summary>
    /// Regenerates the member's persisted status line after this pass changed the tier it
    /// describes. Best-effort with a log: the alerts that triggered this are already stored, and
    /// a status-line failure must not unwind them or fail the member's pass.
    /// </summary>
    private async Task RegenerateStatusLineAsync(Guid memberId, CancellationToken ct)
    {
        try
        {
            await _statusLine.RegenerateAsync(memberId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex,
                "Status line regeneration failed for CardiMember {CardiMemberId}; the alerts were stored.",
                memberId);
        }
    }

    /// <summary>
    /// The model's sentences as a caregiver may read them, or null when they may not: a leftover
    /// name or pronoun token that the record cannot resolve, a sex the record does not bear out,
    /// or nothing at all. A named condition keeps the alert — severity still routes — but swaps
    /// the sentence for <see cref="NonClinicalObservation"/>, exactly as the assessor does.
    /// </summary>
    private static string? CaregiverFacingMessage(string? message, MemberVoice voice)
    {
        if (string.IsNullOrWhiteSpace(message))
            return null;
        if (RewriteCopyGuards.StatesAnUnsupportedSex(message, voice.Gender))
            return null;

        var resolved = voice.Resolve(message.Trim());
        if (string.IsNullOrWhiteSpace(resolved) || MemberVoice.IsUnresolvedIn(resolved))
            return null;

        var truncated = resolved.Length <= MaxMessageLength ? resolved : resolved[..MaxMessageLength];
        return JournalRegisterGuards.NamesACondition(truncated) is null ? truncated : NonClinicalObservation;
    }

    /// <summary>
    /// The model's headline as the card title, held to the title rules every generated headline
    /// on the platform is held to (<see cref="GeneratedTitles"/>), with a leftover name token
    /// dropped rather than resolved into a title. One that fails falls back to the settings
    /// catalogue's own name for the rule — "Activity decline", "Elevated resting heart rate" —
    /// which names the observation the caregiver already chose to be told about, not a verdict.
    /// </summary>
    private static string CaregiverFacingHeadline(string? headline, string rule)
    {
        var cleaned = (headline ?? string.Empty).Trim().Trim('"', '\'', '.', '—', '-').Trim();
        var usable = cleaned.Length is > 0 and <= MaxHeadlineLength
            && !GeneratedTitles.ExceedsWordCap(cleaned)
            && !MemberVoice.IsUnresolvedIn(cleaned);

        return usable ? cleaned : AlertRuleCatalogue.Find(rule)?.Title ?? "Worth a look";
    }

    /// <summary>The alert row's own column widths — see <c>AlertConfiguration</c>.</summary>
    private const int MaxMessageLength = 2000;
    private const int MaxHeadlineLength = 255;

    /// <summary>MedGemma's reply shape for <see cref="JudgementInstructions"/>. Internal, not
    /// Application/DTOs — this describes the private model's reply, not the public API contract;
    /// internal rather than private so the structured call can be exercised in tests.</summary>
    internal sealed record JudgementAiResponse
    {
        public required IReadOnlyList<JudgementVerdict> Verdicts { get; init; }
    }

    /// <summary>
    /// One verdict. Both vocabularies are closed with <see cref="AllowedValuesAttribute"/>, which
    /// <c>StructuredOutputSchema</c> exports as the field's <c>enum</c> and both providers compile
    /// into the decoding grammar.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Asking in prose was not enough, and the brief asks twice — "each carrying the finding's rule
    /// exactly as written", then "rule: the finding's rule, copied exactly". On 2026-09-22 MedGemma
    /// answered <c>activity_decrease</c> for <see cref="StatisticalAlertRules.ActivityDeclineRule"/>
    /// on every pass for one member across an hour, while a second member on the same rule and the
    /// same build matched every time: byte-identical replies each pass, so prompt sensitivity rather
    /// than sampling. <see cref="string.Equals(string?, string?, StringComparison)"/> matched
    /// nothing, the pass failed closed, and the finding was re-judged five minutes later — for ever,
    /// because a verdict that raises nothing is deliberately not persisted.
    /// </para>
    /// <para>
    /// An <c>enum</c> is the difference between asking and constraining: the paraphrase is not a
    /// reachable token rather than a discouraged one. The same failure shape is recorded against
    /// the daily clinical read on 2026-09-13, which answered <c>"urgency": null</c> for a week —
    /// see <c>StructuredOutputSchema.ConstrainToAllowedValues</c>'s remarks.
    /// </para>
    /// <para>
    /// The rule list is every rule in <see cref="StatisticalAlertRules"/>, not merely the ones a
    /// given pass asked about. A per-call schema would be narrower still, but this one is static,
    /// is exported once per process, and closes the branch that actually fired. Matching stays
    /// case-insensitive: the grammar constrains what the model may emit, and the comparison is the
    /// belt to its braces.
    /// </para>
    /// </remarks>
    internal sealed record JudgementVerdict
    {
        [AllowedValues(
            StatisticalAlertRules.ActivityDeclineRule,
            StatisticalAlertRules.IrregularSleepRule,
            StatisticalAlertRules.ElevatedHeartRateRule,
            StatisticalAlertRules.NoMorningActivityRule,
            StatisticalAlertRules.LongTermTrendRule,
            StatisticalAlertRules.HeartRateVariabilityDropRule,
            StatisticalAlertRules.IrregularRhythmRule,
            StatisticalAlertRules.EcgAtrialFibrillationRule,
            StatisticalAlertRules.OvernightBreathingUpRule,
            StatisticalAlertRules.ElevatedZoneWithoutMovementRule,
            StatisticalAlertRules.DaytimeInactivityBlockRule)]
        public required string Rule { get; init; }

        [AllowedValues(
            AssessmentSeverityParser.CriticalSeverity,
            AssessmentSeverityParser.HighSeverity,
            AssessmentSeverityParser.MediumSeverity,
            AssessmentSeverityParser.LowSeverity)]
        public required string Severity { get; init; }

        public required string Headline { get; init; }
        public required string Message { get; init; }
    }

    /// <summary>
    /// One verdict's outcome. Counted at every exit including the successful one, because each of
    /// the four fail-closed exits is only meaningful as a share of the verdicts that were returned
    /// — and a warning that recurs on a five-minute schedule is indistinguishable from background
    /// until it can be divided by that denominator.
    /// </summary>
    private static void CountVerdict(string outcome, string rule) =>
        JudgementTelemetry.Verdicts.Add(
            1,
            new KeyValuePair<string, object?>(JudgementTelemetry.OutcomeTag, outcome),
            new KeyValuePair<string, object?>(JudgementTelemetry.RuleTag, rule));

    /// <summary>
    /// The prompt: the fixed brief, the member's context block, and the findings as a JSON array
    /// of rule, observation and the metric values the rule stored. The metric values ride along
    /// so the model sees the exact figures the detail screen will draw, not only the prose
    /// rendering of them.
    /// </summary>
    internal static string BuildPrompt(
        string instructions, string memberContext, IReadOnlyList<StatisticalFinding> findings)
    {
        var array = new JsonArray();
        foreach (var finding in findings)
        {
            array.Add(new JsonObject
            {
                ["rule"] = finding.Rule,
                ["observation"] = finding.Observation,
                ["figures"] = JsonNode.Parse(finding.MetricValues),
            });
        }

        return $"""
            {instructions}

            [PATIENT CONTEXT]
            {memberContext}

            [FINDINGS]
            {MedicalPromptBlocks.JsonFence(array.ToJsonString(new JsonSerializerOptions { WriteIndented = true }))}
            """;
    }

    private static void AddIfPresent(List<StatisticalFinding> into, StatisticalFinding? finding)
    {
        if (finding is not null)
            into.Add(finding);
    }
}
