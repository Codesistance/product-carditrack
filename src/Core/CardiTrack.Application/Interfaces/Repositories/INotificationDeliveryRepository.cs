using CardiTrack.Domain.Entities;

namespace CardiTrack.Application.Interfaces.Repositories;

public interface INotificationDeliveryRepository : IRepository<NotificationDelivery>
{
    /// <summary>
    /// Atomically claims up to <paramref name="batchSize"/> due rows via <c>FOR UPDATE SKIP
    /// LOCKED</c>, advancing each row's claim-lease (<c>NextAttemptAt</c>) inside the same
    /// statement — not a <c>State</c> transition — so a scaled-out Worker never double-sends —
    /// three Cloud Run instances calling this concurrently divide the outbox instead of racing
    /// over the same rows.
    /// </summary>
    Task<IReadOnlyList<NotificationDelivery>> ClaimDueAsync(
        int batchSize, DateTime utcNow, CancellationToken ct = default);

    Task<NotificationDelivery?> GetByDedupKeyAsync(string dedupKey, CancellationToken ct = default);

    /// <summary>
    /// <c>Sent</c> rows at least <see cref="Services.Notifications.EscalationPolicy.RepushAfter"/> old —
    /// the earliest any stage can take an action, so this excludes rows too young to matter
    /// rather than returning every outstanding Sent row on every tick.
    /// </summary>
    Task<IReadOnlyList<NotificationDelivery>> GetDueForEscalationAsync(
        DateTime utcNow, CancellationToken ct = default);

    /// <summary>Rows past <c>ExpiresAt</c> that never reached a terminal state — the TTL expiry sweep.</summary>
    Task<IReadOnlyList<NotificationDelivery>> GetExpiredAsync(DateTime utcNow, CancellationToken ct = default);
}
