using System.Text.Json;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services;
using CardiTrack.Application.Services.Notifications;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Domain.Extensions;
using CardiTrack.Infrastructure.Extensions;
using CardiTrack.Infrastructure.ExternalClients;
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
/// <para>
/// Silence with a known cause is not reported as silence. When a device's grant has been refused
/// (<see cref="ConnectionStatusExtensions.NeedsReconnect"/>) the watch has not gone quiet — we
/// have lost permission to read it, and "it may need charging" sends the family to fix the wrong
/// thing. So at the moment this pass would have raised the device-silence alert it asks the gap
/// resolver for <c>DEVICE_AUTH_BROKEN</c> instead, and a silence alert already standing is
/// resolved once the grant is found broken. The reconnect nudge is Safety class and pushes, so the
/// caregiver hears "needs reconnecting" at the time they would otherwise have heard "gone quiet".
/// </para>
/// </summary>
public class InactivityDetectionService : IInactivityDetectionService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IDispatchService _dispatch;
    private readonly INotificationGapResolver _gapResolver;
    private readonly IServiceProvider _services;
    private readonly ILogger<InactivityDetectionService> _logger;

    /// <param name="services">
    /// Resolves the sync engine per connection. <see cref="IDeviceSyncService"/> is registered
    /// <em>keyed</em> by <see cref="Domain.Enums.HealthApi"/> — which engine serves a connection
    /// is decided by its device type through configuration — so it cannot be taken as a
    /// constructor dependency: injecting it directly is what broke this worker on every tick
    /// after the probe first shipped. <c>GetDeviceSyncService</c> is the one resolution path.
    /// </param>
    /// <param name="gapResolver">
    /// Opens <c>DEVICE_AUTH_BROKEN</c> for a member whose silence is a refused grant. No cycle:
    /// the resolver depends on neither this service nor <see cref="IDispatchService"/>, and the
    /// push that follows comes from <c>NotificationDispatchWorker</c>'s sweep, not from here.
    /// </param>
    public InactivityDetectionService(
        IUnitOfWork unitOfWork,
        IDispatchService dispatch,
        INotificationGapResolver gapResolver,
        IServiceProvider services,
        ILogger<InactivityDetectionService> logger)
    {
        _unitOfWork = unitOfWork;
        _dispatch = dispatch;
        _gapResolver = gapResolver;
        _services = services;
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
    /// arrived, or whether the provider refused a grant on the way — the self-heal that stands
    /// between a stalled puller and a false alarm.
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
    /// here as "no data", which returns the caller to raising the alert — except a refused grant,
    /// which answers the question outright and is reported as
    /// <see cref="ProbeOutcome.GrantRejected"/>.
    /// </para>
    /// </remarks>
    private async Task<ProbeOutcome> ProbeAsync(
        Guid memberId, DateTime utcNow, InactivityDetectionRules rules, CancellationToken ct)
    {
        // A suspended device is not collecting, so it is not one to probe for signs of life.
        var connections = (await _unitOfWork.DeviceConnections.GetActiveByCardiMemberIdAsync(memberId))
            .Where(c => c.SuspendedAt is null)
            .ToList();
        if (connections.Count == 0)
            return ProbeOutcome.StillSilent;

        var grantRejected = false;

        foreach (var connection in connections)
        {
            ct.ThrowIfCancellationRequested();

            // Which engine serves this connection is configuration, not code — the same
            // DeviceType→HealthApi mapping the sync worker resolves through. A device type no
            // provider block claims cannot be pulled by anyone, so there is nothing to probe with
            // and the alert below is the honest outcome.
            if (_services.GetDeviceSyncService(connection.DeviceType) is not { } sync)
            {
                _logger.LogWarning(
                    "No sync engine is registered for {DeviceType}; DeviceConnection "
                    + "{DeviceConnectionId} cannot be probed before the device-silence alert.",
                    connection.DeviceType, connection.Id);
                continue;
            }

            try
            {
                await sync.SyncCardiMemberAsync(connection);
            }
            catch (DeviceGrantRejectedException ex)
            {
                // The probe's own refresh was the one the provider refused, and the sync path has
                // already retired the connection to TokenExpired. That is the answer to "why is it
                // quiet?", and it is not "the watch" — so the caller hands over to the reconnect
                // nudge rather than raising the silence alert this probe was guarding.
                grantRejected = true;
                _logger.LogWarning(
                    "Inactivity probe found DeviceConnection {DeviceConnectionId}'s grant refused by the "
                    + "provider ({StatusCode}); it needs reconnecting.",
                    connection.Id, (int)ex.StatusCode);
            }
            catch (Exception ex)
            {
                // Not this pass's problem to solve: the sync path records what a failure means for
                // the connection (SyncError, TokenExpired), and a member whose device cannot be
                // reached is exactly who the alert below is for. Still Warning, not Information —
                // an exception is always worth a level someone watching Warning+ would see, even
                // when (as here) the outcome it leads to is expected and handled.
                _logger.LogWarning(
                    ex, "Inactivity probe pull failed for DeviceConnection {DeviceConnectionId}.", connection.Id);
            }
        }

        var lastDataUtc = await LastGranularMinuteAsync(memberId, utcNow, rules.SilenceThresholdMinutes, ct);
        var revived = IsReporting(lastDataUtc, utcNow, rules);

        if (revived)
        {
            _logger.LogInformation(
                "Inactivity probe found readings for CardiMember {CardiMemberId} that the scheduled pull had "
                + "not fetched (latest {LastDataUtc:o}); no device-silence alert raised.",
                memberId, lastDataUtc);
            return ProbeOutcome.Revived;
        }

        // Readings win over a refused grant: a second device still reporting means the member is
        // not dark, whatever happened to the first. The reconnect nudge still reaches that one at
        // the daily gap evaluation; it just does not stand in for a silence alert nobody needs.
        return grantRejected ? ProbeOutcome.GrantRejected : ProbeOutcome.StillSilent;
    }

    /// <summary>What forcing a pull told us about a member believed to be dark.</summary>
    private enum ProbeOutcome
    {
        /// <summary>Nothing arrived, and nothing explained why — the device-silence alert stands.</summary>
        StillSilent,

        /// <summary>Readings landed: the silence was our own puller, not the device.</summary>
        Revived,

        /// <summary>The provider refused a grant while being asked: it needs reconnecting.</summary>
        GrantRejected,
    }

    private async Task<bool> CheckMemberAsync(
        Guid memberId, DateTime utcNow, InactivityDetectionRules rules, CancellationToken ct)
    {
        var member = await _unitOfWork.CardiMembers.GetByIdAsync(memberId);
        if (member is null || !member.IsActive || member.IsMonitoringPaused(utcNow))
            return false;

        // Nobody left to tell, and nobody left to collect for. Asking to delete an account stops
        // monitoring for any member it leaves without a caregiver, and this pass is the one place
        // that reaches the sync service without going through the scheduler that already applies
        // that rule: the probe below calls SyncCardiMemberAsync, which writes DeviceActivityLogs
        // and ActivityLogs rows. Without this check a member whose only caregiver is thirty days
        // into deleting their account keeps being pulled every fifteen minutes — and, at the end
        // of it, can have rows written behind the erasure cascade, which has no foreign key to
        // stop them.
        //
        // The predicate is the scheduler's, exactly: a member with no active link at all is still
        // collected for, here as there. Skipping those too would silently stop a member's
        // inactivity alerts after an ordinary link removal while routine sync carried on filling
        // their history — one collection path disagreeing with another about the same member.
        if (await _unitOfWork.UserCardiMembers.IsLeftUnwatchedByPendingDeletionAsync(memberId))
            return false;

        // Soft-deleted rows still count: removing the card dismisses this silence episode, not
        // the next 15-minute tick of the same dead watch. Resolving when the device reports
        // again is what re-arms the path.
        var existing = (await _unitOfWork.Alerts.GetByCardiMemberAsync(memberId, activeOnly: false)).ToList();
        bool IsThisRule(Alert a) => AlertRuleMarkers.Suppresses(
            a, AlertType.Inactivity, AlertRuleMarkers.DeviceSilenceRule);

        // Read before the episode check, because a refused grant bears on an episode already
        // running as much as on starting one. "Any" rather than "all": with one device refused
        // and another merely quiet, nothing here can tell which one the silence belongs to, and
        // the one action guaranteed to be needed is the reconnect. Once that is done the grant is
        // intact again, and a silence that persists is reported as silence on the next pass.
        var devices = (await _unitOfWork.DeviceConnections.GetActiveByCardiMemberIdAsync(memberId)).ToList();
        var awaitingReconnect = devices.Any(c => c.SuspendedAt is null && c.ConnectionStatus.NeedsReconnect());

        // An episode already running is settled here, above every gate below. Those gates all
        // answer one question — "is now a fair moment to accuse a watch of being dead?" — and
        // none of them bears on whether an accusation already made is over. Closing an alert
        // whose readings have demonstrably come back is never the wrong call, whatever the clock
        // says and whether or not the rule is still switched on.
        //
        // It used to sit below them, and that is #1249: a caregiver who reconnected the watch at
        // nine in the evening was outside waking hours, so the resolve never ran and the "gone
        // quiet" alert stood until nine the next morning. Disconnecting, reconnecting and
        // re-syncing all looked like they did nothing, because each of them lands readings and
        // none of them moves the clock.
        if (existing.Any(IsThisRule))
        {
            var lastData = await LastGranularMinuteAsync(memberId, utcNow, rules.SilenceThresholdMinutes, ct);
            if (IsReporting(lastData, utcNow, rules))
            {
                if (AlertResolution.Resolve(existing, IsThisRule, utcNow) > 0)
                {
                    await _unitOfWork.SaveChangesAsync();
                    // The persisted status line catches up on the next pipeline pass — the Worker
                    // has no medical model to regenerate it here, by design.
                }

                return false;
            }

            // Still silent is the cooldown: one unresolved device-silence alert at a time, or a
            // dead device re-pages every fifteen minutes. Scoped to this rule rather than the
            // whole Inactivity type — the statistical engine's activity-decline alert shares the
            // type but asks for a different action ("encourage movement", not "charge the
            // watch"), and the two may legitimately stand together.
            if (!awaitingReconnect)
                return false;

            // Unless the silence now has a known cause. A grant refused after the alert was raised
            // — the watch sat unworn, then its token lapsed — means "it may need charging" is
            // sending the family to fix the wrong thing, so the alert is closed here, above the
            // waking-hours gate like the resolve above it. The reconnect request below is not: it
            // pushes, so it waits for the same moment a new silence alert would.
            if (AlertResolution.Resolve(existing, IsThisRule, utcNow) > 0)
            {
                await _unitOfWork.SaveChangesAsync();
                _logger.LogInformation(
                    "Resolved the device-silence alert for CardiMember {CardiMemberId}: a device grant "
                    + "was refused, so it needs reconnecting rather than charging.",
                    memberId);
            }
        }

        // A reconnect is not a device-silence alert, so the switch that turns that rule off does
        // not turn this off — it is the Safety nudge a caregiver cannot mute.
        if (!awaitingReconnect)
        {
            var rulePrefs = AlertRuleOverrides.FromJson(
                (await _unitOfWork.AlertPreferences.GetByCardiMemberIdAsync(memberId, ct))?.DisabledRules);
            if (!rulePrefs.IsEnabled(AlertRuleCatalogue.DeviceSilence))
                return false;
        }

        // Every device suspended: collection stopped because a caregiver stopped it, and "the watch
        // has gone quiet" would be telling them something they did themselves. A member with no
        // devices at all is a different case and still falls through.
        if (devices.Count > 0 && devices.All(c => c.SuspendedAt is not null))
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

        var lastDataUtc = await LastGranularMinuteAsync(memberId, utcNow, rules.SilenceThresholdMinutes, ct);
        if (IsReporting(lastDataUtc, utcNow, rules))
            return false;

        // The silence alert's moment, with the reason already known. No probe either: every pull
        // of a refused connection is another request for a token the provider has said no to,
        // and DeviceAuthRecoveryWorker already retries those on a widening backoff.
        if (awaitingReconnect)
        {
            await RequestReconnectAsync(memberId, ct);
            return false;
        }

        // Last check before telling a family their father's watch has stopped: pull now, rather
        // than believing a schedule. Silence at this point means no granular readings have
        // landed — which is a claim about our own puller as much as about the device, and the
        // two are indistinguishable from here. A stalled or lagging pull repairs itself in this
        // call, and no alert is raised; a genuinely quiet watch comes back empty and the alert
        // below is worth the alarm it causes.
        switch (await ProbeAsync(memberId, utcNow, rules, ct))
        {
            case ProbeOutcome.Revived:
                return false;

            // The probe's own refresh was refused. The token lapsed between the scheduled pull and
            // this one, and the silence is that — which is what the caregiver should be told.
            case ProbeOutcome.GrantRejected:
                await RequestReconnectAsync(memberId, ct);
                return false;
        }

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
        // Same as the resolve path above: the status line catches up on the next pipeline pass.

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
    /// Asks the gap resolver to evaluate this member now, which opens <c>DEVICE_AUTH_BROKEN</c>
    /// for the refused connection — the "needs reconnecting" alert, sent in place of "gone quiet".
    /// </summary>
    /// <remarks>
    /// <para>
    /// Raised here, at the silence alert's moment, rather than the instant the sync path records
    /// the refusal. A refused refresh is not proof of a revoked grant (providers answer
    /// <c>invalid_grant</c> during their own incidents), and <c>DeviceAuthRecoveryService</c>'s
    /// first retries land inside the silence threshold — so a refusal that heals on its own never
    /// reaches the caregiver, exactly as that service intends. And since this nudge is Safety class
    /// and pierces quiet hours, the waking-hours gate above is what keeps a token lapsing at 3am
    /// from waking a household over something that can wait until morning.
    /// </para>
    /// <para>
    /// Idempotent: the reconciler converges on the row that already exists, and the dispatch sweep
    /// pushes once per arming, so re-asking on each pass while the member stays dark costs one
    /// evaluation and never a second push. The daily <c>DataCompletenessWorker</c> run remains the
    /// backstop for members this pass never sees — one with no granular series at all.
    /// </para>
    /// </remarks>
    private async Task RequestReconnectAsync(Guid memberId, CancellationToken ct)
    {
        await _gapResolver.ResolveForCardiMemberAsync(memberId, ct);
        _logger.LogInformation(
            "CardiMember {CardiMemberId} is silent because a device grant was refused; asked for the "
            + "reconnect nudge instead of raising a device-silence alert.",
            memberId);
    }

    /// <summary>
    /// Whether the device has produced a reading inside the silence threshold — the single
    /// question both ends of an episode turn on, so both read it the same way.
    /// </summary>
    private static bool IsReporting(
        DateTime? lastDataUtc, DateTime utcNow, InactivityDetectionRules rules) =>
        lastDataUtc is not null && lastDataUtc > utcNow.AddMinutes(-rules.SilenceThresholdMinutes);

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
