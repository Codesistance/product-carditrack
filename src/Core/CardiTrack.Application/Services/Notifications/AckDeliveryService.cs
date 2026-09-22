using System.Diagnostics;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Enums;
using CardiTrack.Shared.Telemetry;

namespace CardiTrack.Application.Services.Notifications;

public interface IAckDeliveryService
{
    /// <summary>
    /// Idempotent — a replayed ack (retransmitted by the OS, or the background handler retrying)
    /// is a no-op, not an error. The caller (the API action) has already validated the ack token
    /// before this runs; this method trusts <paramref name="deliveryId"/>/<paramref
    /// name="pushDeviceTokenId"/> completely.
    /// </summary>
    Task MarkDeliveredAsync(Guid deliveryId, Guid pushDeviceTokenId, CancellationToken ct = default);

    /// <summary>
    /// Stops every outstanding delivery about one alert, because a caregiver answered the alert
    /// itself. Returns how many were stopped.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Answering in the app and acknowledging a push are the same event as far as the escalation
    /// ladder is concerned — somebody has this — but they arrive by different doors, and until now
    /// only the push door closed the ladder. A caregiver who opened the app, read the alert and
    /// dealt with it was still escalated against, and the family got a second and third page about
    /// something already handled.
    /// </para>
    /// <para>
    /// Idempotent: a second caregiver answering finds nothing left unfinished and stops nothing.
    /// </para>
    /// </remarks>
    Task<int> HaltEscalationForAlertAsync(Guid alertId, CancellationToken ct = default);
}

public class AckDeliveryService : IAckDeliveryService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;

    public AckDeliveryService(IUnitOfWork unitOfWork, TimeProvider? timeProvider = null)
    {
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task MarkDeliveredAsync(Guid deliveryId, Guid pushDeviceTokenId, CancellationToken ct = default)
    {
        var delivery = await _unitOfWork.NotificationDeliveries.GetByIdAsync(deliveryId);
        if (delivery is null || delivery.PushDeviceTokenId != pushDeviceTokenId)
            return;

        // Tag and link the ack request's own span (ASP.NET Core auto-instrumentation started it;
        // Application never starts its own here) back to the trace that sent this delivery — a
        // real shared trace id isn't reliable across a gap the escalation ladder can stretch to
        // 900s, so a link is the correlation primitive, not trace-id continuation.
        var current = Activity.Current;
        current?.SetTag(PushDispatchTelemetry.DeliveryIdTag, deliveryId.ToString());
        if (TraceLinking.TryBuildLink(delivery.SendTraceParent) is { } link)
            current?.AddLink(link);

        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;

        // Already delivered — a replay, not a new event. Escalation is already halted; touching
        // DeliveredDate again would just be noise.
        if (delivery.State == DeliveryState.Delivered)
            return;

        delivery.State = DeliveryState.Delivered;
        delivery.DeliveredDate = utcNow;
        _unitOfWork.NotificationDeliveries.Update(delivery);

        // The SLO metric (notification_engine.md §6.1) — p99 for Safety must stay under 60s.
        if (delivery.SentDate is { } sentDate)
        {
            PushDispatchTelemetry.TimeToAck.Record((utcNow - sentDate).TotalSeconds,
                new KeyValuePair<string, object?>(PushDispatchTelemetry.CategoryTag, delivery.Category.ToString()));
        }

        var token = await _unitOfWork.PushDeviceTokens.GetByIdAsync(pushDeviceTokenId);
        if (token is not null)
        {
            token.LastAckDate = utcNow;
            _unitOfWork.PushDeviceTokens.Update(token);
        }

        await _unitOfWork.SaveChangesAsync();
    }

    public async Task<int> HaltEscalationForAlertAsync(Guid alertId, CancellationToken ct = default)
    {
        var unfinished = await _unitOfWork.NotificationDeliveries.GetUnfinishedForAlertAsync(alertId, ct);
        if (unfinished.Count == 0)
            return 0;

        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;

        foreach (var delivery in unfinished)
        {
            // Answered rather than Delivered. Both halt the ladder, but Delivered is a claim
            // about a specific handset posting /delivered, and the time-to-ack SLO is measured
            // from exactly those — counting an in-app answer as one would report a push as having
            // landed on a phone that may have been face-down all night.
            delivery.State = DeliveryState.Answered;
            // DeliveredDate deliberately untouched. It is the record that a specific handset
            // posted /delivered, and most rows reaching here never did — a Pending copy held for
            // somebody's quiet hours has not been sent at all. Stamping it would make delivery
            // reporting claim a push arrived because somebody answered on another device, which
            // is the exact confusion the separate Answered state exists to avoid.
            _unitOfWork.NotificationDeliveries.Update(delivery);
        }

        await _unitOfWork.SaveChangesAsync();
        return unfinished.Count;
    }
}
