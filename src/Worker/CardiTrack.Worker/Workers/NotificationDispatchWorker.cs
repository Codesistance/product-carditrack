using System.Diagnostics;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Services.Notifications;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Diagnostics;
using Microsoft.Extensions.Options;

namespace CardiTrack.Worker.Workers;

/// <summary>
/// Claims due outbox rows, retries them, runs the escalation ladder, and expires past-TTL rows
/// (notification_engine.md §6.2, §6.3, §13).
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately no advisory lock</b> — unlike <see cref="DataCompletenessWorker"/>. That lock
/// serializes an entire run across instances, correct for a once-daily batch; this worker runs
/// every 30 seconds and needs three Cloud Run instances dividing the outbox in parallel, which is
/// exactly what <c>NotificationDeliveryRepository.ClaimDueAsync</c>'s <c>FOR UPDATE SKIP LOCKED</c>
/// claim (plus its claim-lease <c>NextAttemptAt</c> advance) already guarantees on its own.
/// </para>
/// <para>
/// <b>Scope, stated plainly:</b> the daily silent-push liveness probe described in §6.3 ("a daily
/// silent push per device") is not implemented here — it needs a distinct
/// <c>content-available</c>/<c>apns-push-type: background</c> payload variant that doesn't yet
/// exist on <c>INotificationChannel</c>. What this worker does instead is disable tokens that have
/// shown <em>no</em> liveness signal — neither a real delivery ack nor a foreground registration
/// heartbeat (<c>DeviceTokenService.RegisterAsync</c> touches <c>LastSeenDate</c> on every
/// foreground) — in 7 days, which covers an abandoned install but not an active one that simply
/// hasn't had a real alert to ack. The active probe is a tracked follow-up, not silently dropped.
/// </para>
/// </remarks>
public class NotificationDispatchWorker : CronBackgroundService
{
    private const int ClaimBatchSize = 100;

    /// <summary>
    /// Open Safety nudges converted to pushes per tick. Separate from <see cref="ClaimBatchSize"/>
    /// despite the same value — one bounds a retry claim off the outbox, the other a scan of the
    /// notification table, and they have no reason to move together.
    /// </summary>
    private const int PushSweepBatchSize = 100;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<NotificationDispatchWorker> _logger;
    private readonly TimeProvider _timeProvider;

    public NotificationDispatchWorker(
        IOptionsMonitor<WorkerOptions> options,
        IServiceScopeFactory scopeFactory,
        ILogger<NotificationDispatchWorker> logger,
        TimeProvider? timeProvider = null)
        : base(options.Get(nameof(NotificationDispatchWorker)).CronExpression, logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    protected override async Task ExecuteJobAsync(CancellationToken stoppingToken)
    {
        // The trace root for every send this tick drives — without it, RetryClaimedRowsAsync's and
        // RunEscalationSweepAsync's fcm.send spans (most real sends; the API's immediate-send path
        // is the only other producer) would each be an orphaned single-span trace with no context
        // for which tick or phase produced them.
        using var activity = PushTelemetry.Source.StartActivity("notification.dispatch_tick", ActivityKind.Internal);

        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;

        // Each phase is isolated, for the same reason CronBackgroundService guards its
        // run-on-startup invocation: this host does not override BackgroundServiceExceptionBehavior,
        // so anything escaping a tick stops the entire Worker — wearable sync, baselines, partition
        // maintenance and all. That is not theoretical. A misconfigured FirebaseApp made resolving
        // IDispatchService throw on every sweep, and because it throws at *resolution* it lands
        // here rather than in the per-row try/catch below: 224 whole-host restarts in two hours
        // (incident 2026-08-12), during which nothing else the Worker owns ran either.
        //
        // A push outage should cost pushes, nothing more. The phases are independent by
        // construction — each opens its own scope and commits its own work — so a failed one
        // genuinely leaves the others able to run, and every row it would have touched is still
        // due on the next 30-second tick.
        await RunPhaseAsync(nameof(EnqueuePendingNudgePushesAsync), () => EnqueuePendingNudgePushesAsync(utcNow, stoppingToken), stoppingToken);
        await RunPhaseAsync(nameof(RetryClaimedRowsAsync), () => RetryClaimedRowsAsync(utcNow, stoppingToken), stoppingToken);
        await RunPhaseAsync(nameof(RunEscalationSweepAsync), () => RunEscalationSweepAsync(utcNow, stoppingToken), stoppingToken);
        await RunPhaseAsync(nameof(ExpirePastTtlRowsAsync), () => ExpirePastTtlRowsAsync(utcNow, stoppingToken), stoppingToken);
        await RunPhaseAsync(nameof(DisableUnreachableTokensAsync), () => DisableUnreachableTokensAsync(utcNow, stoppingToken), stoppingToken);
    }

    /// <summary>
    /// Runs one phase, logging rather than propagating anything it throws. Cancellation is
    /// deliberately let through — a stopping host is not a phase failure, and swallowing it would
    /// make shutdown wait out the rest of the tick.
    /// </summary>
    private async Task RunPhaseAsync(string phase, Func<Task> run, CancellationToken ct)
    {
        using var activity = PushTelemetry.Source.StartActivity("notification.dispatch_phase", ActivityKind.Internal);
        activity?.SetTag(PushTelemetry.PhaseTag, phase);

        try
        {
            await run();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.GetType().Name);
            // Without this, ExceptionLoggingSpanProcessor finds no "exception" event on the span
            // and silently re-logs nothing for this failure — SetStatus alone isn't enough.
            activity?.AddException(ex);
            _logger.LogError(ex, "NotificationDispatch phase {Phase} failed; the next tick retries it.", phase);
        }
    }

    /// <summary>
    /// Turns an open pushing nudge into a push (§6.2). The rules that qualify declare it
    /// themselves via <c>NudgeSpec.PushesWhenOpen</c> — <c>DEVICE_BATTERY_LOW</c>,
    /// <c>DEVICE_AUTH_BROKEN</c> and <c>DEVICE_STALE_LONG</c> today. The first two are
    /// Safety-class; the third is not, which is why this sweep keys off the flag rather than the
    /// category.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a sweep rather than an enqueue at the point of detection.</b> Every producer of these
    /// rows funnels through <c>NotificationGapResolver</c>, which would be the obvious place to
    /// call <see cref="IDispatchService"/> — except <c>DispatchService</c> already depends on
    /// <c>INotificationGapResolver</c> to arm <c>PUSH_UNREACHABLE</c> after a permanent send
    /// failure, so that edge closes a scoped DI cycle. Reading the rows back here instead keeps the
    /// dependency one-directional, and picks up gaps opened by <em>any</em> of the resolver's nine
    /// call sites plus <c>DataCompletenessWorker</c>'s daily run, rather than the subset someone
    /// remembered to wire.
    /// </para>
    /// <para>
    /// Latency is one tick — at most 30 seconds against a 30-second dispatch SLO, on top of
    /// detection that now happens within a device-sync cycle rather than at 06:00 the next day.
    /// </para>
    /// <para>
    /// <b>Claim, then send.</b> This host runs three Cloud Run instances and this sweep has no
    /// advisory lock or <c>SKIP LOCKED</c> claim, so all three read the same pending rows.
    /// <c>TryClaimForPushAsync</c> is a conditional UPDATE that only one of them can win, which is
    /// what keeps a single flat battery from ringing three phones — the dedup key cannot do it,
    /// since each tick stamps its own timestamp and the three keys differ. A failed enqueue hands
    /// the claim back so the next tick retries rather than the warning being marked delivered and
    /// never sent.
    /// </para>
    /// <para>
    /// <b>One scope per row</b>, matching <see cref="RetryClaimedRowsAsync"/>. A batch sharing one
    /// <c>DbContext</c> would carry a failed <c>SaveChanges</c>'s pending inserts into the next
    /// row's save, so one transient database error would take the rest of the batch with it — for
    /// Safety warnings, that is warnings nobody receives.
    /// </para>
    /// </remarks>
    private async Task EnqueuePendingNudgePushesAsync(DateTime utcNow, CancellationToken ct)
    {
        IReadOnlyList<Domain.Entities.Notification> pending;

        using (var readScope = _scopeFactory.CreateScope())
        {
            var unitOfWork = readScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            pending = await unitOfWork.Notifications.GetPendingPushAsync(
                NudgeRuleCatalogue.PushableRuleCodes, PushSweepBatchSize, ct);
        }

        if (pending.Count == 0)
            return;

        var enqueued = 0;

        foreach (var notification in pending)
        {
            if (ct.IsCancellationRequested)
                break;

            using var scope = _scopeFactory.CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var claimed = false;

            try
            {
                var dispatch = scope.ServiceProvider.GetRequiredService<IDispatchService>();

                claimed = await unitOfWork.Notifications.TryClaimForPushAsync(notification.Id, utcNow, ct);
                if (!claimed)
                    continue;

                await dispatch.EnqueueAsync(new EnqueueRequest(
                    SourceType: DeliverySourceType.Notification,
                    SourceId: notification.Id,
                    UserId: notification.UserId,
                    CardiMemberId: notification.CardiMemberId,
                    // The nudge engine's Safety and the delivery spine's Safety are separate enums
                    // over the same idea; this is the one place they meet. Severity stays null —
                    // it describes a wearer's clinical reading, and none of these are about one.
                    Category: DeliveryCategory.Safety,
                    Severity: null,
                    // Per arming, not per gap: a delivery's dedup key matches in any state and
                    // never expires, so keying on the fingerprint alone would swallow every warning
                    // after the first time this gap ever opened.
                    DedupKey: $"nudge:{notification.Fingerprint}:{utcNow:yyyyMMddHHmmss}",
                    // Per gap, not per arming: on the device, a second warning about the same flat
                    // battery should replace the first rather than stack beneath it.
                    CollapseKey: $"nudge-{notification.Fingerprint}"), ct);

                enqueued++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to enqueue a push for Notification {NotificationId}.", notification.Id);

                if (claimed)
                    await ReleasePushClaimSafelyAsync(unitOfWork, notification.Id, ct);
            }
        }

        if (enqueued > 0)
            _logger.LogInformation("NotificationDispatch enqueued {Count} Safety nudge push(es).", enqueued);
    }

    /// <summary>
    /// Releases a claim whose send failed. Swallows its own failure deliberately: this runs inside a
    /// catch block, and letting it throw would replace the real error with a second one and skip the
    /// remaining rows. A claim that cannot be handed back re-arms when the gap next closes and
    /// returns — worse than a retry next tick, better than losing the batch.
    /// </summary>
    private async Task ReleasePushClaimSafelyAsync(IUnitOfWork unitOfWork, Guid notificationId, CancellationToken ct)
    {
        try
        {
            await unitOfWork.Notifications.ReleasePushClaimAsync(notificationId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Could not release the push claim on Notification {NotificationId}; it will not retry until the gap re-arms.",
                notificationId);
        }
    }

    private async Task RetryClaimedRowsAsync(DateTime utcNow, CancellationToken ct)
    {
        using var claimScope = _scopeFactory.CreateScope();
        var unitOfWork = claimScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var claimed = await unitOfWork.NotificationDeliveries.ClaimDueAsync(ClaimBatchSize, utcNow, ct);
        Activity.Current?.SetTag(PushTelemetry.ClaimedCountTag, claimed.Count);

        if (claimed.Count == 0)
            return;

        _logger.LogInformation("NotificationDispatch claimed {Count} due row(s).", claimed.Count);

        // One scope per row, matching DataCompletenessWorker's per-organization pattern — one bad
        // row (a transient DB error, a cancelled send) must not cost the rest of the batch.
        foreach (var delivery in claimed)
        {
            if (ct.IsCancellationRequested)
                break;

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var dispatch = scope.ServiceProvider.GetRequiredService<IDispatchService>();
                var scopedUow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

                // Re-fetch inside this row's own scope/DbContext — the claimed entity came from a
                // different (now-disposed) scope's context and can't be tracked here directly.
                var owned = await scopedUow.NotificationDeliveries.GetByIdAsync(delivery.Id);
                if (owned is null)
                    continue;

                await dispatch.RetryClaimedAsync(owned, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Retry failed for NotificationDelivery {DeliveryId}.", delivery.Id);
            }
        }
    }

    private async Task RunEscalationSweepAsync(DateTime utcNow, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var dispatch = scope.ServiceProvider.GetRequiredService<IDispatchService>();

        var candidates = await unitOfWork.NotificationDeliveries.GetDueForEscalationAsync(utcNow, ct);
        Activity.Current?.SetTag(PushTelemetry.CandidateCountTag, candidates.Count);

        foreach (var delivery in candidates)
        {
            if (ct.IsCancellationRequested)
                break;

            var action = EscalationPolicy.Evaluate(new EscalationContext
            {
                UtcNow = utcNow,
                Escalates = delivery.Category == DeliveryCategory.Safety
                    || (delivery.Category == DeliveryCategory.Health && delivery.Severity == AlertSeverity.Red),
                CurrentStage = delivery.EscalationStage,
                SentDate = delivery.SentDate
            });

            try
            {
                switch (action)
                {
                    case EscalationAction.Repush:
                        delivery.EscalationStage = EscalationStage.Repushed;
                        await dispatch.RetryClaimedAsync(delivery, ct);
                        PushTelemetry.Escalated.Add(1,
                            new KeyValuePair<string, object?>(PushTelemetry.StageTag, nameof(EscalationStage.Repushed)));
                        break;

                    case EscalationAction.FanOutToOtherCaregivers:
                        delivery.EscalationStage = EscalationStage.FannedOut;
                        unitOfWork.NotificationDeliveries.Update(delivery);
                        await unitOfWork.SaveChangesAsync();
                        await FanOutAsync(delivery, dispatch, unitOfWork, ct);
                        PushTelemetry.Escalated.Add(1,
                            new KeyValuePair<string, object?>(PushTelemetry.StageTag, nameof(EscalationStage.FannedOut)));
                        break;

                    case EscalationAction.MarkUndeliveredCritical:
                        delivery.State = DeliveryState.Undelivered;
                        delivery.EscalationStage = EscalationStage.UndeliveredCritical;
                        unitOfWork.NotificationDeliveries.Update(delivery);
                        await unitOfWork.SaveChangesAsync();
                        PushTelemetry.UndeliveredCritical.Add(1);
                        _logger.LogCritical(
                            "NotificationDelivery {DeliveryId} UNDELIVERED_CRITICAL — no ack from anyone after the full escalation ladder.",
                            delivery.Id);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Escalation step failed for NotificationDelivery {DeliveryId}.", delivery.Id);
            }
        }
    }

    /// <summary>
    /// Copies every other caregiver with <c>ReceiveAlerts</c> on — a no-op in R1 under
    /// <c>MaxUsers = 1</c>, left unconditional rather than special-cased away (§6.3). The
    /// fan-out copy is a rendering concern (never names who failed to respond) and lives in
    /// Mobile, not here — this only creates the additional deliveries.
    /// </summary>
    private static async Task FanOutAsync(
        Domain.Entities.NotificationDelivery original, IDispatchService dispatch, IUnitOfWork unitOfWork, CancellationToken ct)
    {
        if (original.CardiMemberId is not { } cardiMemberId)
            return;

        var links = await unitOfWork.UserCardiMembers.GetByCardiMemberIdAsync(cardiMemberId);
        var otherRecipients = links
            .Where(l => l.IsActive && l.ReceiveAlerts && l.UserId != original.UserId)
            .Select(l => l.UserId)
            .Distinct();

        foreach (var userId in otherRecipients)
        {
            await dispatch.EnqueueAsync(new EnqueueRequest(
                SourceType: original.SourceType,
                SourceId: original.SourceId,
                UserId: userId,
                CardiMemberId: cardiMemberId,
                Category: original.Category,
                Severity: original.Severity,
                DedupKey: $"{original.DedupKey}:escalated:{userId}",
                CollapseKey: original.CollapseKey), ct);
        }
    }

    private async Task ExpirePastTtlRowsAsync(DateTime utcNow, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var expired = await unitOfWork.NotificationDeliveries.GetExpiredAsync(utcNow, ct);
        if (expired.Count == 0)
            return;

        foreach (var delivery in expired)
        {
            delivery.State = DeliveryState.DeadLettered;
            delivery.LastError = "Expired past TTL before a terminal outcome.";
            unitOfWork.NotificationDeliveries.Update(delivery);
        }

        await unitOfWork.SaveChangesAsync();
        _logger.LogInformation("NotificationDispatch expired {Count} past-TTL row(s).", expired.Count);
    }

    private async Task DisableUnreachableTokensAsync(DateTime utcNow, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var stale = await unitOfWork.PushDeviceTokens.GetDueForLivenessProbeAsync(utcNow, ct);
        var floor = utcNow.AddDays(-7);
        var toDisable = stale.Where(t => t.LastSeenDate < floor && (t.LastAckDate is null || t.LastAckDate < floor)).ToList();

        if (toDisable.Count == 0)
            return;

        var gapResolver = scope.ServiceProvider.GetRequiredService<Application.Interfaces.Services.INotificationGapResolver>();

        foreach (var token in toDisable)
        {
            token.DisabledDate = utcNow;
            token.DisabledReason = "No liveness signal (ack or foreground heartbeat) in 7 days.";
            unitOfWork.PushDeviceTokens.Update(token);
            PushTelemetry.TokenChurn.Add(1, new KeyValuePair<string, object?>(PushTelemetry.ReasonTag, "stale"));
        }

        await unitOfWork.SaveChangesAsync();

        foreach (var userId in toDisable.Select(t => t.UserId).Distinct())
            await gapResolver.ResolveForUserAsync(userId, ct);

        _logger.LogInformation("NotificationDispatch disabled {Count} stale push token(s).", toDisable.Count);
    }
}
