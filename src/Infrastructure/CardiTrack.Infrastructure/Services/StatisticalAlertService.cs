using System.Text.Json;
using System.Text.Json.Nodes;
using CardiTrack.Application.Interfaces.Clients;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Domain.Extensions;
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
/// enforced — members without an established baseline are skipped wholesale.
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
/// judged benign is not written anywhere, so it is judged again on the next pass; the dedup
/// bounds that to the passes of one day, and the rules only produce a finding when a yardstick
/// is crossed, so a member with nothing off costs nothing.
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

    public StatisticalAlertService(
        IUnitOfWork unitOfWork,
        IMedicalAiService medicalAi,
        MemberContextComposer memberContext,
        StatusLineGenerationService statusLine,
        ILogger<StatisticalAlertService> logger,
        IAlertNotificationEnqueue? alertEnqueue = null)
    {
        _unitOfWork = unitOfWork;
        _medicalAi = medicalAi;
        _memberContext = memberContext;
        _statusLine = statusLine;
        _logger = logger;
        _alertEnqueue = alertEnqueue;
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

        _logger.LogInformation(
            "Statistical judgement pass complete. Candidates: {Candidates}, alerts raised: {Raised}.",
            memberIds.Count, raised);
        return raised;
    }

    private async Task<int> EvaluateMemberAsync(Guid memberId, DateTime utcNow, CancellationToken ct)
    {
        var member = await _unitOfWork.CardiMembers.GetByIdAsync(memberId);
        if (member is null || !member.IsActive || member.IsMonitoringPaused(utcNow))
            return 0;

        // Established baseline only: no 30-day baseline means every rule stays silent, exactly
        // as the provisional-never-alerts principle demands.
        var baseline = await _unitOfWork.PatternBaselines.GetLatestByCardiMemberAsync(memberId, periodDays: 30);
        if (baseline is null)
            return 0;

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
            && !rulePrefs.IsEnabled(StatisticalAlertRules.DaytimeInactivityBlockRule))
        {
            return 0;
        }

        var timeZone = await MemberAnchorTimeZone.ResolveAsync(_unitOfWork, memberId);
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(utcNow, timeZone);
        var localToday = DateOnly.FromDateTime(localNow);
        var yesterday = localToday.AddDays(-1);

        // One fetch covers every rule: yesterday and today for the daily rules, four trailing
        // weeks for the trend. Stored dates are the wearer's civil days.
        var logsByDate = (await _unitOfWork.ActivityLogs.GetByCardiMemberAndDateRangeAsync(
                memberId, localToday.AddDays(-7 * StatisticalAlertRules.TrendWeeks), localToday))
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

        // NOTE: this pass's alerts are not auto-resolved, and so still latch — see
        // AlertResolution for what that costs. Closing them needs each rule to say whether it was
        // able to judge at all: every rule here returns null both when it did not trip and when
        // its inputs were missing (no reading for yesterday, too few days for the trend), and
        // treating the second as "the episode has passed" would resolve a standing alert on a day
        // that produced no evidence either way. On a health screen that is the wrong failure —
        // better a rule that stays latched than one that quietly stands down in the dark.
        if (findings.Count == 0)
            return 0;

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

        if (toJudge.Count == 0)
            return 0;

        // One call per member per pass, however many findings: the model reads them together,
        // which is the point — a quiet day and a raised overnight vital are one picture, not two.
        var memberContext = await _memberContext.ComposeAsync(
            new MemberContextRequest(
                member, memberId, DateOnly.FromDateTime(utcNow), utcNow, PromptPurpose.StatisticalJudgement),
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
                _logger.LogInformation(
                    "The model judged rule {Rule} on CardiMember {CardiMemberId} not worth attention today.",
                    finding.Rule, memberId);
                continue;
            }

            var message = CaregiverFacingMessage(verdict.Message, voice);
            if (message is null)
            {
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
        }

        if (created.Count == 0)
            return 0;

        await _unitOfWork.SaveChangesAsync();

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

        return created.Count;
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

    internal sealed record JudgementVerdict
    {
        public required string Rule { get; init; }
        public required string Severity { get; init; }
        public required string Headline { get; init; }
        public required string Message { get; init; }
    }

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
