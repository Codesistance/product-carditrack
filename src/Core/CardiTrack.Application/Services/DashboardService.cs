using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Extensions;

namespace CardiTrack.Application.Services;

public class DashboardService : IDashboardService
{
    private const int BaselinePeriodDays = BaselineProgress.PeriodDays;

    /// <summary>
    /// Baseline windows tried longest-first: the established 30-day picture when it exists,
    /// else the best provisional one. The response's baseline state carries which one served,
    /// so a client can caveat what the colours are anchored to.
    /// </summary>
    private static readonly int[] BaselinePeriodPreference = [BaselinePeriodDays, 14, 7];
    private const int RecentAlertCount = 5;

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

        // The established 30-day baseline specifically, not the provisional 14/7-day one that
        // may have won the fallback search above. StatisticalAlertService will not raise a single
        // alert until that same 30 days exists — showing a confident "All steady" any earlier
        // would claim a clean bill of health from the one system not yet watching for a dirty one.
        // Metrics and the baseline-progress card below still read the provisional baseline; only
        // the health-status colour waits for the real one.
        var isLearning = baseline is not { PeriodDays: BaselinePeriodDays };

        var age = member.DateOfBirth.ToAgeInYears(today);
        var metrics = logs.Count == 0 ? null : MemberInsightsCalculator.BuildMetrics(logs, baseline, today, age);
        var unresolvedAlerts = activeAlerts.Where(a => !a.IsResolved).ToList();

        var now = DateTime.UtcNow;
        var isPaused = member.IsMonitoringPaused(now);
        var lastSyncedAt = member.LastSyncDate ?? connections.Max(c => c.LastSyncDate);

        var latestAssessment = await _unitOfWork.RealtimeAssessments.GetLatestAsync(cardiMemberId, ct);
        var (freshnessTier, freshnessMessage) = MemberInsightsCalculator.ComputeDataFreshness(
            lastSyncedAt, latestAssessment?.GeneratedAtUtc, now, FirstNameOf(member.Name));

        // Consent checked before the query, not after — same stance as EnvironmentalContextSource:
        // only a consented member can ever have a row, so the common case skips the roundtrip.
        var environmentalReading = member.EnvironmentalContextConsentGranted
            ? await _unitOfWork.EnvironmentalReadings.GetLatestAsync(cardiMemberId, ct)
            : null;

        return new DashboardResponse
        {
            CardiMemberId = member.Id,
            Name = member.Name,
            Age = age,
            EmergencyContactPhone = member.EmergencyContactPhone,
            EmergencyContactName = member.EmergencyContactName,
            Phone = member.Phone,
            PhotoUrl = null,
            // A paused member is not being watched, so no reassuring colour may be shown for
            // them — stale metrics would otherwise keep reading "doing well" indefinitely.
            HealthStatus = isPaused
                ? PausedStatus
                : MemberInsightsCalculator.ComputeHealthStatus(unresolvedAlerts, isLearning, metrics),
            MonitoringPaused = isPaused,
            MonitoringPausedUntil = isPaused ? member.MonitoringPausedUntil : null,
            MonitoringPauseReason = isPaused ? member.MonitoringPauseReason : null,
            LastSyncedAt = lastSyncedAt,
            DataFreshness = freshnessTier,
            DataFreshnessMessage = freshnessMessage,
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
            Weather = WeatherSnapshotResponse.From(
                member.EnvironmentalContextConsentGranted, environmentalReading),
            RecentAlerts = activeAlerts
                .Take(RecentAlertCount)
                .Select(a => new DashboardAlertSummary
                {
                    AlertId = a.Id,
                    Type = a.AlertType.GetDisplayName(),
                    Severity = MemberInsightsCalculator.SeverityLabel(a.Severity),
                    Title = a.Title,
                    Message = a.Message,
                    TriggeredAt = a.TriggeredDate,
                    IsAcknowledged = a.AcknowledgedDate is not null,
                })
                .ToList(),
            GeneratedAt = DateTime.UtcNow,
        };
    }

    private static string FirstNameOf(string name) =>
        name.Split(' ', StringSplitOptions.RemoveEmptyEntries) is [var first, ..] ? first : name;
}
