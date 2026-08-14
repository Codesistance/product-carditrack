namespace CardiTrack.Application.Interfaces.Clients;

/// <summary>
/// The AI pipeline's transport into the notification engine (notification_engine.md §2): POST
/// <c>/api/v1/internal/notifications/enqueue</c>. The pipeline holds no send stack — recipient
/// resolution, quiet hours, dedup and escalation all happen inside the API, identically to a
/// Worker-raised alert. Implementations must not log the bearer token.
/// </summary>
public interface IAlertNotificationEnqueue
{
    /// <summary>
    /// Asks the API to fan the given alert out to caregivers with <c>ReceiveAlerts</c>. The
    /// caller has already persisted the <c>Alert</c> row; this is delivery, not creation.
    /// </summary>
    Task EnqueueForAlertAsync(Guid alertId, CancellationToken ct = default);
}
