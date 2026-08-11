using CardiTrack.Domain.Entities;

namespace CardiTrack.Application.Interfaces.Repositories;

public interface INotificationDeliveryRepository : IRepository<NotificationDelivery>
{
    /// <summary>
    /// Atomically claims up to <paramref name="batchSize"/> due rows via <c>FOR UPDATE SKIP
    /// LOCKED</c> and marks them <c>Sent</c>-pending (state transition happens inside the same
    /// statement) so a scaled-out Worker never double-sends — three Cloud Run instances calling
    /// this concurrently divide the outbox instead of racing over the same rows.
    /// </summary>
    Task<IReadOnlyList<NotificationDelivery>> ClaimDueAsync(
        int batchSize, DateTime utcNow, CancellationToken ct = default);

    Task<NotificationDelivery?> GetByDedupKeyAsync(string dedupKey, CancellationToken ct = default);

    /// <summary>Rows still <c>Sent</c> past their escalation window — the dispatch worker's timer sweep.</summary>
    Task<IReadOnlyList<NotificationDelivery>> GetDueForEscalationAsync(
        DateTime utcNow, CancellationToken ct = default);

    /// <summary>Rows past <c>ExpiresAt</c> that never reached a terminal state — the TTL expiry sweep.</summary>
    Task<IReadOnlyList<NotificationDelivery>> GetExpiredAsync(DateTime utcNow, CancellationToken ct = default);
}
