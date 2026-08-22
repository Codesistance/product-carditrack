using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services;
using CardiTrack.Application.Services.Notifications;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Extensions;
using Microsoft.Extensions.Logging;

namespace CardiTrack.Infrastructure.Services;

/// <summary>
/// The R1 statistical alert engine (docs/execution/backend/api/alerts.md): every 15 minutes,
/// each recently-active member's daily readings are evaluated by the pure rules in
/// <see cref="StatisticalAlertRules"/>.
/// <para>
/// The rules split in two. <b>Comparative</b> rules judge a reading against the member's own
/// established 30-day baseline, and run only where one exists — that gate is how "provisional
/// baselines never alert" is enforced. <b>Measured</b> rules report a finding the device itself
/// made: an ECG classification, an irregular-rhythm notification, a weighing, a glucose reading.
/// Those need no baseline and are not gated on one, because there is no inference in them to be
/// thin — a watch that tells its wearer it saw atrial fibrillation is not a statistic about them.
/// </para>
/// <para>
/// Two layers keep a 15-minute cadence from paging anyone twice: the rule-scoped cooldown
/// (<see cref="AlertRuleMarkers.Suppresses"/> — one unresolved <em>standing</em> alert per
/// remedy) and a same-local-day dedup (a daily-grain rule that already judged today — whether
/// that alert is still on the list, resolved, or the caregiver deleted it — must not re-fire
/// from the same day's data that evening).
/// </para>
/// </summary>
public class StatisticalAlertService : IStatisticalAlertService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IDispatchService _dispatch;
    private readonly ILogger<StatisticalAlertService> _logger;

    public StatisticalAlertService(
        IUnitOfWork unitOfWork,
        IDispatchService dispatch,
        ILogger<StatisticalAlertService> logger)
    {
        _unitOfWork = unitOfWork;
        _dispatch = dispatch;
        _logger = logger;
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
                // One member's failure must not cost the rest of the fleet this pass.
                _logger.LogError(ex, "Statistical alert evaluation failed for CardiMember {CardiMemberId}.", memberId);
            }
        }

        _logger.LogInformation(
            "Statistical alert pass complete. Candidates: {Candidates}, alerts raised: {Raised}.",
            memberIds.Count, raised);
        return raised;
    }

    private async Task<int> EvaluateMemberAsync(Guid memberId, DateTime utcNow, CancellationToken ct)
    {
        var member = await _unitOfWork.CardiMembers.GetByIdAsync(memberId);
        if (member is null || !member.IsActive || member.IsMonitoringPaused(utcNow))
            return 0;

        // Established baseline only for the rules that compare against one: no 30-day baseline
        // means those stay silent, exactly as the provisional-never-alerts principle demands.
        // It is fetched rather than gated on, because the measured rules below do not need it.
        var baseline = await _unitOfWork.PatternBaselines.GetLatestByCardiMemberAsync(memberId, periodDays: 30);

        var rulePrefs = AlertRuleOverrides.FromJson(
            (await _unitOfWork.AlertPreferences.GetByCardiMemberIdAsync(memberId, ct))?.DisabledRules);

        // Two families of rule, and the difference is what they need to be true before they can
        // speak. A *comparative* rule asks whether a reading has departed from this member's own
        // established pattern, so it cannot run without one. A *measured* rule reports something
        // the device itself determined — a rhythm notification, an ECG classification, a weighing,
        // a glucose reading — and gating those on a 30-day baseline would mean a member who
        // connected their watch a fortnight ago gets no word when it tells them their heart is in
        // atrial fibrillation. Nothing about that finding is statistical, so nothing about it needs
        // a baseline; the provisional-never-alerts principle is about inference from thin windows,
        // and there is no inference here.
        var comparativeRules = new[]
        {
            StatisticalAlertRules.ActivityDeclineRule,
            StatisticalAlertRules.IrregularSleepRule,
            StatisticalAlertRules.ElevatedHeartRateRule,
            StatisticalAlertRules.NoMorningActivityRule,
            StatisticalAlertRules.LongTermTrendRule,
            StatisticalAlertRules.HeartRateVariabilityDropRule,
        };
        var measuredRules = new[]
        {
            StatisticalAlertRules.IrregularRhythmRule,
            StatisticalAlertRules.EcgAtrialFibrillationRule,
            StatisticalAlertRules.RapidWeightGainRule,
            StatisticalAlertRules.BloodSugarOutOfRangeRule,
        };

        var runsComparative = baseline is not null && comparativeRules.Any(rulePrefs.IsEnabled);
        var runsMeasured = measuredRules.Any(rulePrefs.IsEnabled);

        // Prefer skipping timezone + activity-log fetches when nothing could fire.
        if (!runsComparative && !runsMeasured)
            return 0;

        var timeZone = await MemberAnchorTimeZone.ResolveAsync(_unitOfWork, memberId);
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(utcNow, timeZone);
        var localToday = DateOnly.FromDateTime(localNow);
        var yesterday = localToday.AddDays(-1);

        // One fetch covers every rule: yesterday and today for the daily rules, four trailing
        // weeks for the trend. Stored dates are the wearer's civil days. A member with no
        // established baseline reads the shorter window the measured rules need — the trend is not
        // among the rules that can fire for them, so four weeks of rows would be fetched to be
        // ignored, on every pass, for every member still learning.
        var windowDays = runsComparative
            ? 7 * StatisticalAlertRules.TrendWeeks
            : StatisticalAlertRules.WeightGainLongWindowDays + 1;
        var logsByDate = (await _unitOfWork.ActivityLogs.GetByCardiMemberAndDateRangeAsync(
                memberId, localToday.AddDays(-windowDays), localToday))
            .ToDictionary(l => l.Date);
        var yesterdayLog = logsByDate.GetValueOrDefault(yesterday);
        var todayLog = logsByDate.GetValueOrDefault(localToday);

        // Sleep sessions are attributed to the civil day they ENDED on, so last night lives on
        // today's log, not yesterday's — the dashboard's sleep card rates the same row, and the
        // two surfaces must not disagree about which night "last night" is. Yesterday's log is
        // the fallback for a night whose data only arrived after local midnight; the per-night
        // dedup below is what keeps that fallback from re-judging a night that already alerted.
        var lastNightLog = todayLog?.SleepMinutes is not null ? todayLog : yesterdayLog;

        // Off = do not evaluate at all (not merely suppress the raise). Absence of a preference
        // row means every rule is on.
        // The night before last night, for the HRV rule — one low night is noise, two is a signal.
        // Sleep-derived readings follow the same "ended on this day" attribution as sleep itself,
        // so the night before last night lives on the row before last night's.
        var previousNightLog = logsByDate.GetValueOrDefault(lastNightLog?.Date.AddDays(-1) ?? yesterday);

        var candidates = new List<StatisticalAlertCandidate>();

        // Measured rules first: they run whether or not this member has an established baseline,
        // and the order they are added in is the order a caregiver meets them.
        if (rulePrefs.IsEnabled(StatisticalAlertRules.EcgAtrialFibrillationRule))
            AddIfPresent(candidates, StatisticalAlertRules.EcgAtrialFibrillation(todayLog, yesterdayLog));
        if (rulePrefs.IsEnabled(StatisticalAlertRules.IrregularRhythmRule))
            AddIfPresent(candidates, StatisticalAlertRules.IrregularRhythm(todayLog, yesterdayLog));
        if (rulePrefs.IsEnabled(StatisticalAlertRules.BloodSugarOutOfRangeRule))
            AddIfPresent(candidates, StatisticalAlertRules.BloodSugarOutOfRange(todayLog, yesterdayLog));
        if (rulePrefs.IsEnabled(StatisticalAlertRules.RapidWeightGainRule))
            AddIfPresent(candidates, StatisticalAlertRules.RapidWeightGain(logsByDate, localToday));

        if (baseline is not null)
        {
            if (rulePrefs.IsEnabled(StatisticalAlertRules.ActivityDeclineRule))
                AddIfPresent(candidates, StatisticalAlertRules.ActivityDecline(baseline, yesterdayLog));
            if (rulePrefs.IsEnabled(StatisticalAlertRules.IrregularSleepRule))
            {
                // Age against the member's own local today, the same day the readings are dated in —
                // the sleep rule grades the night on the published band for their age bracket.
                AddIfPresent(candidates, StatisticalAlertRules.IrregularSleep(
                    baseline, lastNightLog, member.DateOfBirth.ToAgeInYears(localToday)));
            }
            if (rulePrefs.IsEnabled(StatisticalAlertRules.ElevatedHeartRateRule))
                AddIfPresent(candidates, StatisticalAlertRules.ElevatedHeartRate(baseline, yesterdayLog));
            if (rulePrefs.IsEnabled(StatisticalAlertRules.NoMorningActivityRule))
                AddIfPresent(candidates, StatisticalAlertRules.NoMorningActivity(baseline, todayLog, localNow));
            if (rulePrefs.IsEnabled(StatisticalAlertRules.LongTermTrendRule))
                AddIfPresent(candidates, StatisticalAlertRules.LongTermTrend(logsByDate, yesterday));
            if (rulePrefs.IsEnabled(StatisticalAlertRules.HeartRateVariabilityDropRule))
            {
                AddIfPresent(candidates, StatisticalAlertRules.HeartRateVariabilityDrop(
                    baseline, lastNightLog, previousNightLog));
            }
        }

        // NOTE: this engine's alerts are not auto-resolved, and so still latch — see
        // AlertResolution for what that costs. Closing them needs each rule to say whether it was
        // able to judge at all: every rule here returns null both when it did not trip and when
        // its inputs were missing (no reading for yesterday, too few days for the trend), and
        // treating the second as "the episode has passed" would resolve a standing alert on a day
        // that produced no evidence either way. On a health screen that is the wrong failure —
        // better a rule that stays latched than one that quietly stands down in the dark.
        if (candidates.Count == 0)
            return 0;

        // Soft-deleted rows are part of the history a daily rule already judged. Fetching only
        // standing alerts meant deleting a card re-armed the same quieter day on the next tick
        // — the caregiver's housekeeping became a new page. Cooldown still looks at standing
        // rows only: this engine does not auto-resolve, so a deleted alert must not latch the
        // rule forever. Same-data dedup below reads the full history.
        var history = (await _unitOfWork.Alerts.GetByCardiMemberAsync(memberId, activeOnly: false)).ToList();
        var standing = history.Where(a => a.IsActive).ToList();

        var created = new List<Alert>();
        foreach (var candidate in candidates)
        {
            if (standing.Any(a => AlertRuleMarkers.Suppresses(a, candidate.Type, candidate.Rule)))
                continue;

            bool FiredOnLocalToday(Alert a) =>
                DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(a.TriggeredDate, timeZone)) == localToday;

            // Same-data dedup, regardless of resolution or deletion: a rule reads one day's data,
            // so one day's data gets at most one alert from it — resolving or deleting at noon
            // must not re-page at half past from the same readings. A candidate that names the
            // night it judged dedups on that night rather than the firing day, because
            // late-arriving data can put the same night in front of the rule on two calendar
            // days; an alert from before night markers existed cannot say which night it judged
            // and is read as today's.
            var judgedAlready = candidate.NightOf is { } night
                ? history.Any(a => AlertRuleMarkers.HasRule(a, candidate.Rule)
                    && (AlertRuleMarkers.HasNight(a, night)
                        || (!AlertRuleMarkers.HasAnyNight(a) && FiredOnLocalToday(a))))
                : history.Any(a => AlertRuleMarkers.HasRule(a, candidate.Rule) && FiredOnLocalToday(a));
            if (judgedAlready)
                continue;

            ct.ThrowIfCancellationRequested();
            var alert = new Alert
            {
                CardiMemberId = memberId,
                AlertType = candidate.Type,
                Severity = candidate.Severity,
                Title = candidate.Title,
                Message = candidate.Message,
                TriggeredDate = utcNow,
                MetricValues = candidate.MetricValues,
            };
            await _unitOfWork.Alerts.AddAsync(alert);
            created.Add(alert);
        }

        if (created.Count > 0)
        {
            await _unitOfWork.SaveChangesAsync();

            // A newly-raised alert can move the member's severity tier, and the persisted status
            // line was generated against whatever tier was current when the pipeline last ran.
            // The Worker cannot regenerate it (no medical model here by design), so the line
            // catches up on the next digest/assess pass — minutes, against copy whose old cache
            // TTL already tolerated fifteen.

            // Push dispatch (notification_engine.md Phase 3) — the same direct-service-call
            // pattern INotificationGapResolver already establishes, not a domain event (this
            // solution has none). One bad dispatch must not cost the batch the alerts it already
            // persisted; DispatchService's own dedup means a retried call here is harmless.
            foreach (var alert in created)
            {
                try
                {
                    await _dispatch.EnqueueForAlertAsync(alert.Id, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Push dispatch failed for Alert {AlertId}.", alert.Id);
                }
            }
        }

        return created.Count;
    }

    private static void AddIfPresent(List<StatisticalAlertCandidate> into, StatisticalAlertCandidate? candidate)
    {
        if (candidate is not null)
            into.Add(candidate);
    }
}
