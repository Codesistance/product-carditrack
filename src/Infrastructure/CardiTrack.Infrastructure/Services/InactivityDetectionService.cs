using System.Text.Json;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services.Notifications;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace CardiTrack.Infrastructure.Services;

/// <summary>
/// The device-silence failsafe (docs/llm_design.md `InactivityDetector`). Every generated
/// artifact in the pipeline deliberately refuses to speak from silence — the digest skips,
/// the assessor skips — so a watch that dies would otherwise produce nothing at all. This
/// pass turns that nothing into exactly one yellow alert.
/// <para>
/// Silence means <b>no granular readings</b>, not "no successful sync": a sync that completes
/// and returns no new minutes is precisely the dead-battery / watch-on-the-nightstand case
/// this alert exists to catch. And it only counts during the member's waking hours, on their
/// anchor clock — overnight silence is a charging watch, not an emergency.
/// </para>
/// </summary>
public class InactivityDetectionService : IInactivityDetectionService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IDispatchService _dispatch;
    private readonly IDeviceSyncService _deviceSync;
    private readonly ILogger<InactivityDetectionService> _logger;

    public InactivityDetectionService(
        IUnitOfWork unitOfWork,
        IDispatchService dispatch,
        IDeviceSyncService deviceSync,
        ILogger<InactivityDetectionService> logger)
    {
        _unitOfWork = unitOfWork;
        _dispatch = dispatch;
        _deviceSync = deviceSync;
        _logger = logger;
    }

    public async Task<int> DetectAsync(
        DateTime utcNow, InactivityDetectionRules rules, CancellationToken ct = default)
    {
        // Nonsense rules must fail loud, not quietly misfire: a zero threshold would alert on
        // every gap between samples, and inverted waking hours would silence the check forever.
        if (rules.SilenceThresholdMinutes <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(rules), rules.SilenceThresholdMinutes, "SilenceThresholdMinutes must be positive.");
        if (rules.WakingStartHour < 0 || rules.WakingEndHour > 24 || rules.WakingStartHour >= rules.WakingEndHour)
            throw new ArgumentOutOfRangeException(
                nameof(rules), $"{rules.WakingStartHour}..{rules.WakingEndHour}",
                "Waking hours must satisfy 0 <= start < end <= 24.");

        // The same candidate filter as the digest and the assessor: active members with data in
        // the last two days. A member silent for longer has aged out of candidacy — and already
        // has their standing alert from the pass that watched them go quiet. A brand-new
        // connection that has never produced data never qualifies; there is nothing to miss yet.
        var since = DateOnly.FromDateTime(utcNow).AddDays(-2);
        var memberIds = (await _unitOfWork.CardiMembers.GetActiveIdsWithActivitySinceAsync(since)).ToList();

        var raised = 0;
        foreach (var memberId in memberIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (await CheckMemberAsync(memberId, utcNow, rules, ct))
                    raised++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Shutdown, not a member failure: swallowing this would log a spurious error
                // and stumble through one more loop iteration instead of stopping cleanly.
                throw;
            }
            catch (Exception ex)
            {
                // One member's failure must not cost the rest of the fleet this pass; the next
                // 15-minute run retries naturally.
                _logger.LogError(ex, "Inactivity check failed for CardiMember {CardiMemberId}.", memberId);
            }
        }

        _logger.LogInformation(
            "Inactivity detection complete. Candidates: {Candidates}, alerts raised: {Raised}.",
            memberIds.Count, raised);
        return raised;
    }

    /// <summary>
    /// Forces a pull for every syncable connection this member has, and reports whether readings
    /// arrived — the self-heal that stands between a stalled puller and a false alarm.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The alert this guards says "no readings from the device", and until now that was inferred
    /// entirely from what our own pull loop had managed to fetch. A connection that stalled, a
    /// worker revision that died mid-window, a provider that was briefly slow — each looks exactly
    /// like a watch on a nightstand, and each would have sent a family to check on someone who was
    /// perfectly fine. The most expensive thing this product can be is wrong in that direction.
    /// </para>
    /// <para>
    /// Routine scope, not the worker cadence: this is a "is anything there?" probe on the trailing
    /// window, and it runs at most once per member per pass, gated behind the silence threshold
    /// and the cooldown above — so it costs a provider request only for members already believed
    /// to be dark. A pull that throws is left to the sync path's own status handling and treated
    /// here as "no data", which returns the caller to raising the alert.
    /// </para>
    /// </remarks>
    private async Task<bool> ProbedIntoLifeAsync(
        Guid memberId, DateTime utcNow, InactivityDetectionRules rules, CancellationToken ct)
    {
        var connections = (await _unitOfWork.DeviceConnections.GetActiveByCardiMemberIdAsync(memberId)).ToList();
        if (connections.Count == 0)
            return false;

        foreach (var connection in connections)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await _deviceSync.SyncCardiMemberAsync(connection);
            }
            catch (Exception ex)
            {
                // Not this pass's problem to solve: the sync path records what a failure means for
                // the connection (SyncError, TokenExpired), and a member whose device cannot be
                // reached is exactly who the alert below is for.
                _logger.LogInformation(
                    ex, "Inactivity probe pull failed for DeviceConnection {DeviceConnectionId}.", connection.Id);
            }
        }

        var lastDataUtc = await LastGranularMinuteAsync(memberId, utcNow, rules.SilenceThresholdMinutes, ct);
        var revived = lastDataUtc is not null && lastDataUtc > utcNow.AddMinutes(-rules.SilenceThresholdMinutes);

        if (revived)
        {
            _logger.LogInformation(
                "Inactivity probe found readings for CardiMember {CardiMemberId} that the scheduled pull had "
                + "not fetched (latest {LastDataUtc:o}); no device-silence alert raised.",
                memberId, lastDataUtc);
        }

        return revived;
    }

    private async Task<bool> CheckMemberAsync(
        Guid memberId, DateTime utcNow, InactivityDetectionRules rules, CancellationToken ct)
    {
        var member = await _unitOfWork.CardiMembers.GetByIdAsync(memberId);
        if (member is null || !member.IsActive || member.IsMonitoringPaused(utcNow))
            return false;

        var timeZone = await MemberAnchorTimeZone.ResolveAsync(_unitOfWork, memberId);
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(utcNow, timeZone);

        // The whole silence window must sit inside waking hours, so alerting effectively starts
        // at wakingStart + threshold (09:00 on the defaults). At 07:30 the trailing two hours
        // are mostly night — flagging a watch that is still on its charger would make the very
        // first alert of the day a false one, and the first one is the one that sets trust.
        var minutesOfDay = localNow.Hour * 60 + localNow.Minute;
        if (minutesOfDay < rules.WakingStartHour * 60 + rules.SilenceThresholdMinutes
            || minutesOfDay >= rules.WakingEndHour * 60)
        {
            return false;
        }

        var existing = (await _unitOfWork.Alerts.GetByCardiMemberAsync(memberId, activeOnly: true)).ToList();
        var lastDataUtc = await LastGranularMinuteAsync(memberId, utcNow, rules.SilenceThresholdMinutes, ct);

        if (lastDataUtc is not null && lastDataUtc > utcNow.AddMinutes(-rules.SilenceThresholdMinutes))
        {
            // The device is reporting again, which is exactly this alert's episode ending. Closing
            // it here is what re-arms the cooldown below: nothing else in the system resolves an
            // alert, so without this the first silence a member ever had would suppress every one
            // after it, for good.
            if (AlertResolution.Resolve(
                    existing,
                    a => AlertRuleMarkers.Suppresses(
                        a, AlertType.Inactivity, AlertRuleMarkers.DeviceSilenceRule),
                    utcNow) > 0)
            {
                await _unitOfWork.SaveChangesAsync();
            }

            return false;
        }

        // Cooldown: one unresolved device-silence alert at a time — a dead device would
        // otherwise re-page every 15 minutes; resolving the alert re-arms the check. Scoped to
        // this rule, not the whole Inactivity type: the statistical engine's activity-decline
        // alert shares the type but asks for a different action ("encourage movement", not
        // "charge the watch"), and the two may legitimately stand together.
        if (existing.Any(a => AlertRuleMarkers.Suppresses(
                a, AlertType.Inactivity, AlertRuleMarkers.DeviceSilenceRule)))
        {
            return false;
        }

        // Last check before telling a family their father's watch has stopped: pull now, rather
        // than believing a schedule. Silence at this point means no granular readings have
        // landed — which is a claim about our own puller as much as about the device, and the
        // two are indistinguishable from here. A stalled or lagging pull repairs itself in this
        // call, and no alert is raised; a genuinely quiet watch comes back empty and the alert
        // below is worth the alarm it causes.
        if (await ProbedIntoLifeAsync(memberId, utcNow, rules, ct))
            return false;

        var silentSince = lastDataUtc is null
            ? "for several hours"
            : $"since {TimeZoneInfo.ConvertTimeFromUtc(lastDataUtc.Value, timeZone):HH:mm}";
        var alert = new Alert
        {
            CardiMemberId = memberId,
            AlertType = AlertType.Inactivity,
            Severity = AlertSeverity.Yellow,
            Title = "Device has gone quiet",
            Message = $"No readings from the device {silentSince}. "
                      + "It may need charging, or a quick check that it is being worn.",
            TriggeredDate = utcNow,
            MetricValues = JsonSerializer.Serialize(new
            {
                rule = AlertRuleMarkers.DeviceSilenceRule,
                lastDataUtc,
                thresholdMinutes = rules.SilenceThresholdMinutes,
            }),
        };
        await _unitOfWork.Alerts.AddAsync(alert);
        await _unitOfWork.SaveChangesAsync();

        // Yellow severity routes to in-app + digest only (§3) — DeliveryPlanner enforces that,
        // not this call site. Still enqueued so every alert flows through the same outbox (§3:
        // "Both produce NotificationDelivery rows... without merging two domain models").
        try
        {
            await _dispatch.EnqueueForAlertAsync(alert.Id, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Push dispatch failed for Alert {AlertId}.", alert.Id);
        }

        return true;
    }

    /// <summary>
    /// The end of the member's most recent minute with any granular reading at all, or null if
    /// the fetched range holds none. The range covers the silence threshold plus an hour of
    /// slack (whole-hour bounds, per the granular read contract) — precision beyond "silent
    /// longer than the threshold" changes nothing here.
    /// </summary>
    private async Task<DateTime?> LastGranularMinuteAsync(
        Guid memberId, DateTime utcNow, int thresholdMinutes, CancellationToken ct)
    {
        // Rounded-up threshold + 1 hour: `utcNow` sits at most an hour before `rangeEnd`, so
        // this is the tightest whole-hour range that always contains the threshold boundary —
        // data old enough to fall before it is older than the threshold by construction.
        var rangeEnd = new DateTime(utcNow.Year, utcNow.Month, utcNow.Day, utcNow.Hour, 0, 0, DateTimeKind.Utc)
            .AddHours(1);
        var rangeStart = rangeEnd.AddHours(-((thresholdMinutes + 59) / 60 + 1));
        var window = await _unitOfWork.GranularMetrics.GetWindowAsync(memberId, rangeStart, rangeEnd, ct);

        var lastIndex = -1;
        foreach (var series in window.MinuteSeries.Values)
        {
            var index = Array.FindLastIndex(series, v => v.HasValue);
            if (index > lastIndex)
                lastIndex = index;
        }

        return lastIndex >= 0 ? rangeStart.AddMinutes(lastIndex + 1) : null;
    }
}
