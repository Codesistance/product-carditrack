using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Interfaces.Clients;
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
    private readonly IProfilePhotoStorage _photoStorage;
    private readonly IQuestionnaireService _questionnaires;

    public DashboardService(
        IUnitOfWork unitOfWork,
        ICardiMemberAccessService access,
        IProfilePhotoStorage photoStorage,
        IQuestionnaireService questionnaires)
    {
        _unitOfWork = unitOfWork;
        _access = access;
        _photoStorage = photoStorage;
        _questionnaires = questionnaires;
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

        // One read rather than every active alert: the status colour, the unread count and the
        // Recent Alerts strip are all readings of the same unresolved set. Producers that resolve
        // alerts still use the tracked GetByCardiMemberAsync.
        var unresolvedAlerts = await _unitOfWork.Alerts.GetUnresolvedByCardiMemberAsync(cardiMemberId);

        // One aggregate, not a second history load: the quiet stretch only needs the newest
        // TriggeredDate, and it counts resolved and deleted rows the read above deliberately
        // filters out — see IAlertRepository.GetLastTriggeredDateAsync.
        var lastAlertAt = await _unitOfWork.Alerts.GetLastTriggeredDateAsync(cardiMemberId, ct);

        // The established 30-day baseline specifically, not the provisional 14/7-day one that
        // may have won the fallback search above. StatisticalAlertService will not raise a single
        // alert until that same 30 days exists — showing a confident "All steady" any earlier
        // would claim a clean bill of health from the one system not yet watching for a dirty one.
        // Metrics and the baseline-progress card below still read the provisional baseline; only
        // the health-status colour waits for the real one.
        var isLearning = baseline is not { PeriodDays: BaselinePeriodDays };

        var age = member.DateOfBirth.ToAgeInYears(today);
        var metrics = logs.Count == 0 ? null : MemberInsightsCalculator.BuildMetrics(logs, baseline, today, age);

        var now = DateTime.UtcNow;
        var isPaused = member.IsMonitoringPaused(now);
        var lastSyncedAt = member.LastSyncDate ?? connections.Max(c => c.LastSyncDate);

        var latestAssessment = await _unitOfWork.RealtimeAssessments.GetLatestAsync(cardiMemberId, ct);
        var (freshnessTier, freshnessMessage) = MemberInsightsCalculator.ComputeDataFreshness(
            lastSyncedAt, latestAssessment?.GeneratedAtUtc, now, FirstNameOf(member.Name));

        // Everything the verdict needs is already resolved above — no extra read but the one
        // aggregate. Evaluated here rather than in the response initialiser because the response
        // is where it is *shown*, and the decision about whether we are entitled to say it at all
        // is worth reading on its own line.
        var quiet = QuietStretch.Evaluate(new QuietStretchContext
        {
            UtcNow = now,
            IsMonitoringPaused = isPaused,
            HasEstablishedBaseline = !isLearning,
            HasUnresolvedAlerts = unresolvedAlerts.Count > 0,
            DataFreshnessTier = freshnessTier,
            LastAlertAtUtc = lastAlertAt,
            MonitoringSinceUtc = member.CreatedDate,
        });

        // Consent checked before the query, not after — same stance as EnvironmentalContextSource:
        // only a consented member can ever have a row, so the common case skips the roundtrip.
        var environmentalReading = member.EnvironmentalContextConsentGranted
            ? await _unitOfWork.EnvironmentalReadings.GetLatestAsync(cardiMemberId, ct)
            : null;

        // Same guard HealthInsightService.GetAdviseAsync applies before serving a row: a paused
        // member's stored suggestion is withheld there, and a pulsing badge promising one the
        // details screen will never show would be worse than no badge at all.
        var advise = isPaused ? null : await _unitOfWork.MemberAdvises.GetByCardiMemberAsync(cardiMemberId);
        // The same predicate the details card and member chat serve on, so the dot cannot pulse for
        // a row either of them would withhold — it used to light on age alone, including for a row
        // citing no reference that the card is contracted not to render.
        var hasAdvise = AdviseServability.IsServable(advise, DateTime.UtcNow);

        // GetPendingAsync re-checks access on its own — a second round trip, since access was
        // already required above — but a cheap one against the caller's small set of linked
        // members, not GetForMemberAsync's whole-history load, which is the cost this avoids.
        var pendingQuestionnaire = await _questionnaires.GetPendingAsync(requestingUserId, cardiMemberId, ct);

        return new DashboardResponse
        {
            CardiMemberId = member.Id,
            Name = member.Name,
            Age = age,
            EmergencyContactPhone = member.EmergencyContactPhone,
            EmergencyContactName = member.EmergencyContactName,
            Phone = member.Phone,
            PhotoUrl = member.PhotoObjectName is null
                ? null
                : await _photoStorage.GetReadUrlAsync(member.PhotoObjectName, ct),
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
            HasAdvise = hasAdvise,
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
            Weather = WeatherSnapshotMapper.From(
                member.EnvironmentalContextConsentGranted, environmentalReading),
            PendingQuestionnaire = pendingQuestionnaire,
            // Unresolved only, newest first. This strip used to be built from every alert whose
            // row was still IsActive — which is the soft-delete flag, and nothing about an
            // episode ending touches it. Acknowledging records who looked and resolution closes
            // the episode; neither deactivated the row, so an alert the producer had already
            // called over sat here until five newer ones pushed it out, on the same screen whose
            // status colour had long since moved on. Same read HealthStatus and UnreadAlertCount
            // above already made, and the same one HealthInsightService makes for the hero line.
            RecentAlerts = unresolvedAlerts
                .Take(RecentAlertCount)
                .Select(a => new DashboardAlertSummary
                {
                    AlertId = a.Id,
                    Type = a.AlertType.GetDisplayName(),
                    Severity = MemberInsightsCalculator.SeverityLabel(a.Severity),
                    Title = a.Title,
                    Message = a.Message,
                    TriggeredAt = a.TriggeredDate,
                    Status = AlertLifecycle.StatusLabel(a),
                })
                .ToList(),
            Reassurance = quiet.IsQuiet
                ? new ReassuranceResponse
                {
                    QuietDays = quiet.Days,
                    QuietSince = quiet.SinceUtc,
                    FollowsAnAlert = lastAlertAt is not null,
                }
                : null,
            GeneratedAt = DateTime.UtcNow,
        };
    }

    private static string FirstNameOf(string name) =>
        name.Split(' ', StringSplitOptions.RemoveEmptyEntries) is [var first, ..] ? first : name;
}
