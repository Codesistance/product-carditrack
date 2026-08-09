using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Domain.Extensions;

namespace CardiTrack.Application.Services;

public class DashboardService : IDashboardService
{
    // Deviation-from-baseline thresholds for per-metric status colouring; consistent with
    // the "medium" alert sensitivity in docs/execution/backend/api/alerts.md.
    private const decimal YellowDeviationPercent = 30m;
    private const decimal OrangeDeviationPercent = 50m;

    private const int BaselinePeriodDays = BaselineProgress.PeriodDays;

    /// <summary>
    /// Baseline windows tried longest-first: the established 30-day picture when it exists,
    /// else the best provisional one. The response's baseline state carries which one served,
    /// so a client can caveat what the colours are anchored to.
    /// </summary>
    private static readonly int[] BaselinePeriodPreference = [BaselinePeriodDays, 14, 7];
    private const int SeriesDays = 7;
    private const int RecentAlertCount = 5;
    private const decimal DefaultStepsGoal = 10000m;

    /// <summary>
    /// Hero status for a member whose monitoring is paused. Outside the green/yellow/orange/red
    /// severity scale on purpose — it says "we are not watching", not "we looked and it's fine".
    /// </summary>
    private const string PausedStatus = "paused";

    private readonly IUnitOfWork _unitOfWork;
    private readonly ICardiMemberAccessService _access;

    public DashboardService(IUnitOfWork unitOfWork, ICardiMemberAccessService access)
    {
        _unitOfWork = unitOfWork;
        _access = access;
    }

    public async Task<DashboardResponse> GetDashboardAsync(
        Guid requestingUserId, Guid cardiMemberId, CancellationToken ct = default)
    {
        await _access.RequireViewAccessAsync(requestingUserId, cardiMemberId, ct);

        var member = await _unitOfWork.CardiMembers.GetByIdAsync(cardiMemberId);
        if (member is null || !member.IsActive)
            throw new KeyNotFoundException("CardiMember not found");

        var connections = (await _unitOfWork.DeviceConnections.GetActiveByCardiMemberIdAsync(cardiMemberId)).ToList();
        var primaryConnection = connections.FirstOrDefault(c => c.IsPrimary) ?? connections.FirstOrDefault();

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var logs = (await _unitOfWork.ActivityLogs.GetByCardiMemberAndDateRangeAsync(
                cardiMemberId, today.AddDays(-(BaselinePeriodDays - 1)), today))
            .ToList();

        // Sequential, not Task.WhenAll — these run on the request's single DbContext.
        PatternBaseline? baseline = null;
        foreach (var periodDays in BaselinePeriodPreference)
        {
            baseline = await _unitOfWork.PatternBaselines.GetLatestByCardiMemberAsync(cardiMemberId, periodDays);
            if (baseline is not null)
                break;
        }

        var activeAlerts = (await _unitOfWork.Alerts.GetByCardiMemberAsync(cardiMemberId, activeOnly: true)).ToList();

        var isLearning = baseline is null;

        var metrics = logs.Count == 0 ? null : BuildMetrics(logs, baseline, today);
        var unresolvedAlerts = activeAlerts.Where(a => !a.IsResolved).ToList();

        var now = DateTime.UtcNow;
        var isPaused = member.IsMonitoringPaused(now);

        return new DashboardResponse
        {
            CardiMemberId = member.Id,
            Name = member.Name,
            Age = member.DateOfBirth.ToAgeInYears(today),
            EmergencyContactPhone = member.EmergencyContactPhone,
            EmergencyContactName = member.EmergencyContactName,
            PhotoUrl = null,
            // A paused member is not being watched, so no reassuring colour may be shown for
            // them — stale metrics would otherwise keep reading "doing well" indefinitely.
            HealthStatus = isPaused ? PausedStatus : ComputeHealthStatus(unresolvedAlerts, isLearning, metrics),
            MonitoringPaused = isPaused,
            MonitoringPausedUntil = isPaused ? member.MonitoringPausedUntil : null,
            MonitoringPauseReason = isPaused ? member.MonitoringPauseReason : null,
            LastSyncedAt = member.LastSyncDate ?? connections.Max(c => c.LastSyncDate),
            UnreadAlertCount = unresolvedAlerts.Count(a => a.AcknowledgedDate is null),
            Device = new DashboardDeviceState
            {
                HasActiveConnection = connections.Count > 0,
                DeviceType = primaryConnection?.DeviceType.GetDisplayName(),
                DeviceName = primaryConnection?.DeviceName,
                ConnectionStatus = primaryConnection?.ConnectionStatus.ToString(),
                LastSyncDate = primaryConnection?.LastSyncDate,
            },
            Baseline = BaselineProgress.From(logs, baseline),
            Metrics = metrics,
            RecentAlerts = activeAlerts
                .Take(RecentAlertCount)
                .Select(a => new DashboardAlertSummary
                {
                    AlertId = a.Id,
                    Type = a.AlertType.GetDisplayName(),
                    Severity = SeverityLabel(a.Severity),
                    Title = a.Title,
                    Message = a.Message,
                    TriggeredAt = a.TriggeredDate,
                    IsAcknowledged = a.AcknowledgedDate is not null,
                })
                .ToList(),
            GeneratedAt = DateTime.UtcNow,
        };
    }

    /// <summary>
    /// Builds the three Key Metrics cards from a member's daily history.
    /// </summary>
    /// <remarks>
    /// Each metric is resolved independently, down the days newest-first, rather than all three
    /// reading a single "latest row". Ingestion stores the day in progress, so today's row appears
    /// as soon as the provider reports anything at all — and a row carrying steps but not yet a
    /// resting heart rate would blank the cards that were populated a moment ago if they all had
    /// to come from the same day. This is the same coalescing rule <see cref="ActivityLogMerge"/>
    /// applies across a member's devices, applied across days.
    /// </remarks>
    private static DashboardMetrics BuildMetrics(List<ActivityLog> logs, PatternBaseline? baseline, DateOnly today)
    {
        var byDate = logs
            .GroupBy(l => l.Date)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(l => l.UpdatedDate ?? l.CreatedDate).First());
        var newestFirst = byDate.OrderByDescending(entry => entry.Key).Select(entry => entry.Value).ToList();

        var latestSteps = LatestWith(newestFirst, l => l.Steps);
        var steps = BuildMetric(
            value: latestSteps?.Steps,
            baselineValue: baseline?.AvgSteps,
            unit: "steps",
            series: BuildSeries(byDate, today, l => l.Steps),
            // Steps accumulate through the day, so a day still in progress has nothing to compare
            // against a whole-day average — at breakfast every member alive would read as a
            // catastrophic drop. The goal bar carries the partial day instead, which is honest
            // about being partway through by construction.
            comparable: latestSteps?.Date != today);
        steps.Goal = baseline?.AvgSteps ?? DefaultStepsGoal;

        // Resting heart rate and sleep are daily summary values, not running totals: the provider
        // either has one for the day or does not. A today reading is a whole reading, so unlike
        // steps it stays comparable against the baseline.
        var latestHeartRate = LatestWith(newestFirst, l => l.RestingHeartRate);
        var heartRate = BuildMetric(
            value: latestHeartRate?.RestingHeartRate,
            baselineValue: baseline?.AvgRestingHeartRate,
            unit: "bpm",
            series: BuildSeries(byDate, today, l => l.RestingHeartRate));
        if (baseline?.AvgRestingHeartRate is int avgHr && baseline.StdDevHeartRate is decimal stdHr)
        {
            heartRate.RangeLow = (int)Math.Round(avgHr - stdHr, MidpointRounding.AwayFromZero);
            heartRate.RangeHigh = (int)Math.Round(avgHr + stdHr, MidpointRounding.AwayFromZero);
        }

        var latestSleep = LatestWith(newestFirst, l => l.SleepMinutes);
        var sleep = BuildMetric(
            value: latestSleep?.SleepMinutes is int sm ? Math.Round(sm / 60m, 1) : null,
            baselineValue: baseline?.AvgSleepMinutes is int abm ? Math.Round(abm / 60m, 1) : null,
            unit: "hours",
            series: BuildSeries(byDate, today, l => l.SleepMinutes is int m ? Math.Round(m / 60m, 1) : (decimal?)null));
        // Read off the same night as the duration above, so the stars can never describe the
        // quality of one night next to the length of another.
        sleep.QualityScore = latestSleep?.SleepEfficiency switch
        {
            null => null,
            >= 90 => 5,
            >= 80 => 4,
            >= 70 => 3,
            >= 60 => 2,
            _ => 1,
        };

        return new DashboardMetrics { Steps = steps, RestingHeartRate = heartRate, Sleep = sleep };
    }

    /// <summary>The most recent day that actually reported this metric, or null when none did.</summary>
    private static ActivityLog? LatestWith<T>(IReadOnlyList<ActivityLog> newestFirst, Func<ActivityLog, T?> select)
        where T : struct =>
        newestFirst.FirstOrDefault(log => select(log).HasValue);

    /// <param name="comparable">
    /// False when the reading covers a period that is not over yet, which makes a
    /// baseline comparison meaningless rather than merely uncertain. Leaves both
    /// <see cref="DashboardMetric.ChangePercent"/> and the derived status unset, so the client
    /// falls back to the card's plain presentation instead of colouring a number it cannot judge.
    /// </param>
    private static DashboardMetric BuildMetric(
        decimal? value, decimal? baselineValue, string unit, List<MetricPoint> series, bool comparable = true)
    {
        decimal? changePercent = null;
        if (comparable && value is not null && baselineValue is > 0)
            changePercent = Math.Round((value.Value - baselineValue.Value) / baselineValue.Value * 100m, 1);

        return new DashboardMetric
        {
            Value = value,
            Baseline = baselineValue,
            ChangePercent = changePercent,
            Unit = unit,
            Status = changePercent is null
                ? "unknown"
                : Math.Abs(changePercent.Value) switch
                {
                    <= YellowDeviationPercent => "green",
                    <= OrangeDeviationPercent => "yellow",
                    _ => "orange",
                },
            Series = series,
        };
    }

    private static List<MetricPoint> BuildSeries(
        Dictionary<DateOnly, ActivityLog> byDate, DateOnly today, Func<ActivityLog, decimal?> selector)
    {
        var series = new List<MetricPoint>(SeriesDays);
        for (var offset = SeriesDays - 1; offset >= 0; offset--)
        {
            var date = today.AddDays(-offset);
            series.Add(new MetricPoint
            {
                Date = date,
                Value = byDate.TryGetValue(date, out var log) ? selector(log) : null,
            });
        }
        return series;
    }

    private static string ComputeHealthStatus(List<Alert> unresolvedAlerts, bool isLearning, DashboardMetrics? metrics)
    {
        if (unresolvedAlerts.Count > 0)
        {
            var worst = unresolvedAlerts.Max(a => a.Severity);
            if (worst >= AlertSeverity.Yellow)
                return SeverityLabel(worst);
        }
        return isLearning || metrics is null ? "unknown" : "green";
    }

    private static string SeverityLabel(AlertSeverity severity) =>
        severity.ToString().ToLowerInvariant();
}
