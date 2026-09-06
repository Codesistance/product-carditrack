using System.Text.Json;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services;
using CardiTrack.Application.Services.Notifications;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace CardiTrack.Infrastructure.Services;

/// <summary>
/// The user-defined alarm engine. Every five minutes it resolves each member's effective alarms —
/// account-level defaults plus their own overrides — cuts their readings into datapoints, and asks
/// <see cref="MetricAlarmEvaluator"/> for a verdict.
/// <para>
/// <b>An alert is written on the transition into alarm, never on the state.</b> The stored
/// <see cref="MetricAlarmState"/> is what makes that distinction possible, and it is also why this
/// producer needs no cooldown of its own: a condition that stays true keeps the alarm standing and
/// stays quiet, and only a return to normal re-arms it. That is deliberately not the alert
/// lifecycle — a caregiver acknowledging a card is saying they have read it, not that the heart
/// rate has come down.
/// </para>
/// <para>
/// <b>Two reads per member, not two per alarm.</b> Every sub-daily alarm the member has is served
/// from one minute-series fetch sized to the longest of them, and every daily alarm from one
/// activity-log fetch. A member with eight alarms costs the same queries as a member with one.
/// </para>
/// </summary>
public class MetricAlarmEngine : IMetricAlarmEngine
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IDispatchService _dispatch;
    private readonly ILogger<MetricAlarmEngine> _logger;

    public MetricAlarmEngine(
        IUnitOfWork unitOfWork,
        IDispatchService dispatch,
        ILogger<MetricAlarmEngine> logger)
    {
        _unitOfWork = unitOfWork;
        _dispatch = dispatch;
        _logger = logger;
    }

    public async Task<int> EvaluateAsync(DateTime utcNow, CancellationToken ct = default)
    {
        // The outer filter. Without it every tick would walk the whole estate to discover that
        // almost nobody has defined an alarm.
        var organizations = (await _unitOfWork.MetricAlarms.GetOrganizationIdsWithEnabledAlarmsAsync(ct))
            .ToHashSet();
        if (organizations.Count == 0)
            return 0;

        var since = DateOnly.FromDateTime(utcNow).AddDays(-2);
        var memberIds = await _unitOfWork.CardiMembers.GetActiveIdsWithActivitySinceAsync(since, organizations);

        var raised = 0;
        foreach (var memberId in memberIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                raised += await EvaluateMemberAsync(memberId, organizations, utcNow, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Shutdown, not a member failure.
                throw;
            }
            catch (Exception ex)
            {
                // One member's failure must not cost the rest of the fleet this pass. Catching is
                // not enough for that: the scope is shared, so whatever this member's save left in
                // the change tracker would be retried — and fail the same way — inside every later
                // member's save.
                _logger.LogError(ex, "Metric alarm evaluation failed for CardiMember {CardiMemberId}.", memberId);
                _unitOfWork.ClearTracking();
            }
        }

        _logger.LogInformation("Metric alarm pass complete. Alerts raised: {Raised}.", raised);
        return raised;
    }

    private async Task<int> EvaluateMemberAsync(
        Guid memberId, IReadOnlySet<Guid> organizations, DateTime utcNow, CancellationToken ct)
    {
        var member = await _unitOfWork.CardiMembers.GetByIdAsync(memberId);
        if (member is null || !member.IsActive || member.IsMonitoringPaused(utcNow))
            return 0;

        // The query was already scoped to these organizations; this is the engine's own guarantee
        // that it never evaluates a member whose organization has no alarms, whatever fed it.
        if (!organizations.Contains(member.OrganizationId))
            return 0;

        var rows = await _unitOfWork.MetricAlarms.GetForMemberAsync(member.OrganizationId, memberId, ct);
        var effective = MetricAlarmResolution.Evaluable(MetricAlarmResolution.Resolve(rows, memberId));
        if (effective.Count == 0)
            return 0;

        var alarms = effective.Select(e => e.Alarm).ToList();
        var states = (await _unitOfWork.MetricAlarmStates.GetByCardiMemberAsync(memberId, ct))
            .ToDictionary(s => s.MetricAlarmId);

        // Established 30-day baseline only, and only when something actually needs it. The
        // provisional-never-alerts principle is enforced the same way it is for the built-in rules:
        // by what the engine fetches. A baseline-relative alarm on a member without an established
        // row resolves to no threshold and reports insufficient data.
        PatternBaseline? baseline = null;
        if (alarms.Any(a => a.ThresholdKind != AlarmThresholdKind.Absolute))
            baseline = await _unitOfWork.PatternBaselines.GetLatestByCardiMemberAsync(memberId, periodDays: 30);

        var granular = alarms.Where(a => SourceOf(a) == AlarmMetricSource.Granular).ToList();
        var daily = alarms.Where(a => SourceOf(a) == AlarmMetricSource.Daily).ToList();

        var window = granular.Count > 0 ? await FetchWindowAsync(memberId, granular, utcNow, ct) : null;

        IReadOnlyDictionary<DateOnly, ActivityLog> logsByDate = new Dictionary<DateOnly, ActivityLog>();
        var localToday = DateOnly.FromDateTime(utcNow);
        if (daily.Count > 0)
        {
            var timeZone = await MemberAnchorTimeZone.ResolveAsync(_unitOfWork, memberId);
            localToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utcNow, timeZone));
            var days = daily.Max(MetricAlarmWindowing.RequiredDays);
            logsByDate = (await _unitOfWork.ActivityLogs.GetByCardiMemberAndDateRangeAsync(
                    memberId, localToday.AddDays(-days), localToday))
                .ToDictionary(l => l.Date);
        }

        var created = new List<Alert>();
        foreach (var alarm in alarms)
        {
            ct.ThrowIfCancellationRequested();

            var previous = states.GetValueOrDefault(alarm.Id);
            var previousState = previous?.State ?? AlarmEvaluationState.InsufficientData;

            // Armed means no alert from the current episode is outstanding. LastAlertId is set on
            // the transition into alarm and cleared on the return to Ok, so a standing episode that
            // dips through InsufficientData — the watch off for a quarter of an hour — is still the
            // same episode when the readings come back, and is not paged about twice.
            var armed = previous?.LastAlertId is null;

            var datapoints = SourceOf(alarm) == AlarmMetricSource.Granular
                ? Slice(alarm, window, utcNow)
                : MetricAlarmWindowing.FromDailyLogs(alarm, logsByDate, localToday);

            var verdict = MetricAlarmEvaluator.Evaluate(alarm, datapoints, baseline, previousState, armed);

            Alert? alert = null;
            if (verdict.RaisedNow)
            {
                alert = BuildAlert(member, alarm, verdict, utcNow);
                await _unitOfWork.Alerts.AddAsync(alert);
                created.Add(alert);
            }

            await RecordStateAsync(previous, alarm, memberId, verdict, alert, utcNow);
        }

        // Every transition recorded this tick has to be saved before the next tick reads it — above
        // all the Ok that lets a later breach read as a transition rather than as a condition that
        // was already standing. A tick that changed nothing has nothing pending and costs no round
        // trip here.
        await _unitOfWork.SaveChangesAsync();

        // Forget this member's rows before moving to the next. The scope is shared across the whole
        // pass, and a tracker that keeps every member's entities makes each later save scan all of
        // them; nothing below needs what was loaded above.
        _unitOfWork.ClearTracking();

        // Push dispatch, the same direct call the statistical engine makes. One bad dispatch must
        // not cost the batch the alerts it already persisted; DispatchService dedups, so a retried
        // call is harmless.
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

        return created.Count;
    }

    private async Task<GranularWindow?> FetchWindowAsync(
        Guid memberId, IReadOnlyList<MetricAlarm> granular, DateTime utcNow, CancellationToken ct)
    {
        var minutes = granular.Max(MetricAlarmWindowing.RequiredMinutes);

        // The store hands back whole hours, so widen to hour bounds and let the windowing anchor
        // itself inside what comes back.
        var to = Floor(utcNow).AddHours(1);
        var from = Floor(utcNow.AddMinutes(-minutes));

        return await _unitOfWork.GranularMetrics.GetWindowAsync(memberId, from, to, ct);

        static DateTime Floor(DateTime t) => new(t.Year, t.Month, t.Day, t.Hour, 0, 0, DateTimeKind.Utc);
    }

    private static IReadOnlyList<AlarmDatapoint> Slice(MetricAlarm alarm, GranularWindow? window, DateTime utcNow)
    {
        if (window is null)
            return Enumerable.Repeat(new AlarmDatapoint(null), alarm.EvaluationPeriods).ToList();

        var definition = AlarmMetricCatalogue.Find(alarm.Metric);
        if (definition?.Backing is not { } backing)
            return Enumerable.Repeat(new AlarmDatapoint(null), alarm.EvaluationPeriods).ToList();

        var series = window.MinuteSeries.GetValueOrDefault(backing);
        var steps = alarm.ContextGate == AlarmContextGate.Inactive
            ? window.MinuteSeries.GetValueOrDefault(GranularMetric.Steps)
            : null;

        return MetricAlarmWindowing.FromMinuteSeries(alarm, series, steps, window.FromUtc, utcNow);
    }

    private static AlarmMetricSource SourceOf(MetricAlarm alarm) =>
        AlarmMetricCatalogue.Find(alarm.Metric)?.Source ?? AlarmMetricSource.Daily;

    private static Alert BuildAlert(CardiMember member, MetricAlarm alarm, AlarmVerdict verdict, DateTime utcNow) =>
        new()
        {
            CardiMemberId = member.Id,
            AlertType = AlertTypeFor(alarm.Metric),
            Severity = alarm.Severity,
            Title = MetricAlarmNarrative.AlertTitle(alarm),
            Message = MetricAlarmNarrative.AlertMessage(alarm, verdict),
            TriggeredDate = utcNow,
            MetricValues = JsonSerializer.Serialize(new
            {
                // The rule marker every producer stamps. Namespaced by the alarm's own id, because
                // a user-defined alarm has no compile-time catalogue entry to be named by — and
                // because two alarms on the same metric are two separate findings that must not
                // suppress or dedup against each other.
                rule = CustomRule(alarm.Id),
                alarmId = alarm.Id,
                alarmName = alarm.Name,
                metric = alarm.Metric.ToString(),
                statistic = alarm.Statistic.ToString(),
                comparison = alarm.Operator.ToString(),
                thresholdKind = alarm.ThresholdKind.ToString(),
                configuredThreshold = alarm.ThresholdValue,
                effectiveThreshold = verdict.EffectiveThreshold,
                observedValue = verdict.ObservedValue,
                periodMinutes = alarm.PeriodMinutes,
                evaluationPeriods = alarm.EvaluationPeriods,
                datapointsToAlarm = alarm.DatapointsToAlarm,
                breachingDatapoints = verdict.BreachingDatapoints,
                condition = MetricAlarmNarrative.Condition(alarm),
            }),
        };

    /// <summary>The <c>rule</c> marker a custom alarm stamps: its own id, namespaced.</summary>
    public static string CustomRule(Guid alarmId) => $"{AlertRuleMarkers.CustomRulePrefix}{alarmId}";

    /// <summary>
    /// Which of the five <see cref="AlertType"/> values a custom alarm's alert is filed under. The
    /// type drives the detail screen's icon and the family-facing grouping, so a heart alarm has to
    /// land on <see cref="AlertType.HeartRate"/> even though this producer's own cooldown is the
    /// state row rather than the type — which is why <see cref="AlertRuleMarkers"/> keeps custom
    /// alerts out of the type-scoped heart cooldown the other producers share.
    /// </summary>
    private static AlertType AlertTypeFor(AlarmMetric metric) => metric switch
    {
        AlarmMetric.HeartRate or AlarmMetric.RestingHeartRate
            or AlarmMetric.HeartRateVariability or AlarmMetric.OvernightHeartRateVariability
            or AlarmMetric.ElevatedZoneMinutes => AlertType.HeartRate,

        AlarmMetric.SleepMinutes => AlertType.Sleep,

        AlarmMetric.Steps or AlarmMetric.DailySteps or AlarmMetric.ActiveZoneMinutes
            or AlarmMetric.LongestSedentaryStretchMinutes => AlertType.Inactivity,

        // Blood oxygen and overnight breathing have no type of their own in the shipped enum, and
        // inventing one would change a wire contract every client already reads. PatternBreak is
        // where the built-in overnight-breathing rule already files, so they keep company.
        _ => AlertType.PatternBreak,
    };

    private async Task RecordStateAsync(
        MetricAlarmState? previous, MetricAlarm alarm, Guid memberId,
        AlarmVerdict verdict, Alert? alert, DateTime utcNow)
    {
        if (previous is null)
        {
            await _unitOfWork.MetricAlarmStates.AddAsync(new MetricAlarmState
            {
                MetricAlarmId = alarm.Id,
                CardiMemberId = memberId,
                State = verdict.State,
                StateSinceUtc = utcNow,
                LastEvaluatedUtc = utcNow,
                LastAlertId = alert?.Id,
            });
            return;
        }

        var transitioned = previous.State != verdict.State;
        if (transitioned)
        {
            previous.State = verdict.State;
            previous.StateSinceUtc = utcNow;

            // Back to normal is the re-arm. Anything short of it — a dip into InsufficientData —
            // keeps the episode's alert on record so the return to alarm is not paged again.
            if (verdict.State == AlarmEvaluationState.Ok)
                previous.LastAlertId = null;
        }

        if (alert is not null)
            previous.LastAlertId = alert.Id;

        // A quiet tick has nothing to record but "still looking". Worth keeping — it is how an
        // alarm that has silently stopped being evaluated is told apart from one that is merely
        // quiet — but not worth a row update every five minutes for every alarm of every member,
        // so it is refreshed hourly. A transition is always written.
        if (!transitioned && utcNow - previous.LastEvaluatedUtc < EvaluatedStampInterval)
            return;

        previous.LastEvaluatedUtc = utcNow;
        _unitOfWork.MetricAlarmStates.Update(previous);
    }

    /// <summary>How often a state row that has not changed is still stamped as evaluated.</summary>
    private static readonly TimeSpan EvaluatedStampInterval = TimeSpan.FromHours(1);
}
