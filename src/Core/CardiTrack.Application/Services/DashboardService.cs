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

    private const int BaselinePeriodDays = 30;
    private const int SeriesDays = 7;
    private const int RecentAlertCount = 5;
    private const decimal DefaultStepsGoal = 10000m;

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

        var baseline = await _unitOfWork.PatternBaselines.GetLatestByCardiMemberAsync(cardiMemberId, BaselinePeriodDays);
        var activeAlerts = (await _unitOfWork.Alerts.GetByCardiMemberAsync(cardiMemberId, activeOnly: true)).ToList();

        var daysCaptured = Math.Min(logs.Select(l => l.Date).Distinct().Count(), BaselinePeriodDays);
        var isLearning = baseline is null;

        var metrics = logs.Count == 0 ? null : BuildMetrics(logs, baseline, today);
        var unresolvedAlerts = activeAlerts.Where(a => !a.IsResolved).ToList();

        return new DashboardResponse
        {
            CardiMemberId = member.Id,
            Name = member.Name,
            Age = member.DateOfBirth.ToAgeInYears(today),
            Phone = member.Phone,
            PhotoUrl = null,
            HealthStatus = ComputeHealthStatus(unresolvedAlerts, isLearning, metrics),
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
            Baseline = new DashboardBaselineState
            {
                IsLearning = isLearning,
                DaysCaptured = daysCaptured,
                DaysRequired = BaselinePeriodDays,
                PercentComplete = Math.Min(100, daysCaptured * 100 / BaselinePeriodDays),
            },
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

    private static DashboardMetrics BuildMetrics(List<ActivityLog> logs, PatternBaseline? baseline, DateOnly today)
    {
        var byDate = logs
            .GroupBy(l => l.Date)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(l => l.UpdatedDate ?? l.CreatedDate).First());
        var latest = byDate[byDate.Keys.Max()];

        var steps = BuildMetric(
            value: latest.Steps,
            baselineValue: baseline?.AvgSteps,
            unit: "steps",
            series: BuildSeries(byDate, today, l => l.Steps));
        steps.Goal = baseline?.AvgSteps ?? DefaultStepsGoal;

        var heartRate = BuildMetric(
            value: latest.RestingHeartRate,
            baselineValue: baseline?.AvgRestingHeartRate,
            unit: "bpm",
            series: BuildSeries(byDate, today, l => l.RestingHeartRate));
        if (baseline?.AvgRestingHeartRate is int avgHr && baseline.StdDevHeartRate is decimal stdHr)
        {
            heartRate.RangeLow = (int)Math.Round(avgHr - stdHr, MidpointRounding.AwayFromZero);
            heartRate.RangeHigh = (int)Math.Round(avgHr + stdHr, MidpointRounding.AwayFromZero);
        }

        var sleep = BuildMetric(
            value: latest.SleepMinutes is int sm ? Math.Round(sm / 60m, 1) : null,
            baselineValue: baseline?.AvgSleepMinutes is int abm ? Math.Round(abm / 60m, 1) : null,
            unit: "hours",
            series: BuildSeries(byDate, today, l => l.SleepMinutes is int m ? Math.Round(m / 60m, 1) : (decimal?)null));
        sleep.QualityScore = latest.SleepEfficiency switch
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

    private static DashboardMetric BuildMetric(decimal? value, decimal? baselineValue, string unit, List<MetricPoint> series)
    {
        decimal? changePercent = null;
        if (value is not null && baselineValue is > 0)
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
