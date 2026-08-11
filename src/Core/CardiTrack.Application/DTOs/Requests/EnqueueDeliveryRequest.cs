namespace CardiTrack.Application.DTOs.Requests;

/// <summary>
/// POST /api/v1/internal/notifications/enqueue — service-to-service only, called by the AI
/// pipeline's SeverityRouter once it has written an <see cref="Domain.Entities.Alert"/> row.
/// Deliberately carries only <see cref="AlertId"/>: which users get notified is always resolved
/// server-side from <c>UserCardiMember.ReceiveAlerts</c>, never trusted from the caller — the
/// endpoint pins the caller's identity (§7.2 C4), not the audience (notification_engine.md §2 —
/// "gets a transport, not a copy of the rules engine").
/// </summary>
public class EnqueueDeliveryRequest
{
    public Guid AlertId { get; set; }
}
