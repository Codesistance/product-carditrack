using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Enums;

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

        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;

        // Already delivered — a replay, not a new event. Escalation is already halted; touching
        // DeliveredDate again would just be noise.
        if (delivery.State == DeliveryState.Delivered)
            return;

        delivery.State = DeliveryState.Delivered;
        delivery.DeliveredDate = utcNow;
        _unitOfWork.NotificationDeliveries.Update(delivery);

        var token = await _unitOfWork.PushDeviceTokens.GetByIdAsync(pushDeviceTokenId);
        if (token is not null)
        {
            token.LastAckDate = utcNow;
            _unitOfWork.PushDeviceTokens.Update(token);
        }

        await _unitOfWork.SaveChangesAsync();
    }
}
