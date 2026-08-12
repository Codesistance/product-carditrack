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
        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;

        await RetryClaimedRowsAsync(utcNow, stoppingToken);
        await RunEscalationSweepAsync(utcNow, stoppingToken);
        await ExpirePastTtlRowsAsync(utcNow, stoppingToken);
        await DisableUnreachableTokensAsync(utcNow, stoppingToken);
    }

    private async Task RetryClaimedRowsAsync(DateTime utcNow, CancellationToken ct)
    {
        using var claimScope = _scopeFactory.CreateScope();
        var unitOfWork = claimScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var claimed = await unitOfWork.NotificationDeliveries.ClaimDueAsync(ClaimBatchSize, utcNow, ct);

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
