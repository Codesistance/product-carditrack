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
    /// Every delivery this alert's answer stopped — the rows sitting in
    /// <see cref="Domain.Enums.DeliveryState.Answered"/>.
    /// </summary>
    Task<IReadOnlyList<NotificationDelivery>> GetAnsweredForAlertAsync(
        Guid alertId, CancellationToken ct = default);

    /// <summary>
    /// The users who already have a delivery row for this alert, whatever state it is in.
    /// </summary>
    /// <remarks>
    /// Asked by the fan-out rung, which exists to reach caregivers the original send did not.
    /// Every state counts, including the terminal ones: somebody whose copy was suppressed or
    /// dead-lettered was still addressed, and re-addressing them is a second push about one event
    /// rather than the cover the rung is for.
    /// </remarks>
    Task<IReadOnlyList<Guid>> GetNotifiedUserIdsForAlertAsync(
        Guid alertId, CancellationToken ct = default);

    /// <summary>
    /// Every delivery about one alert that has not reached a terminal state — what is left to
    /// stop when somebody answers the alert itself.
    /// </summary>
    /// <remarks>
    /// Pending rows as well as Sent ones. A copy deferred to the end of a caregiver's quiet hours
    /// is the one most worth catching: left alone it pushes at 06:00 about something dealt with
    /// at midnight, which reads as the product not knowing what its own family has already done.
    /// </remarks>
    Task<IReadOnlyList<NotificationDelivery>> GetUnfinishedForAlertAsync(
        Guid alertId, CancellationToken ct = default);

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
