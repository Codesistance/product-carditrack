using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Services.Notifications;
using CardiTrack.Domain.Entities;
using CardiTrack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CardiTrack.Infrastructure.Repositories;

public class NotificationDeliveryRepository : Repository<NotificationDelivery>, INotificationDeliveryRepository
{
    public NotificationDeliveryRepository(CardiTrackDbContext context) : base(context)
    {
    }

    /// <summary>
    /// Claim lease: how long a claimed row is excluded from re-claiming while the worker that
    /// claimed it is still processing it. Longer than the 30-second dispatch tick so one slow send
    /// doesn't get double-processed by the next tick on the same or another instance; short enough
    /// that a crashed worker's claimed rows self-heal back into the claimable set quickly rather
    /// than needing a separate lock-expiry sweep.
    /// </summary>
    private static readonly TimeSpan ClaimLease = TimeSpan.FromSeconds(90);

    /// <summary>
    /// <c>UPDATE ... FOR UPDATE SKIP LOCKED ... RETURNING</c> in one round trip — deliberately not
    /// an advisory lock (see <c>NotificationDispatchWorker</c>'s remarks): three Cloud Run
    /// instances calling this concurrently divide the outbox instead of racing over the same rows,
    /// which is the whole reason the 30-second dispatch loop scales horizontally at all. EF's
    /// <c>FromSqlInterpolated</c> materializes entities from any result set shaped like the table,
    /// including an <c>UPDATE ... RETURNING</c> — this is not a SELECT dressed up, it is the actual
    /// claim, atomic with the row lock.
    /// </summary>
    /// <remarks>
    /// <c>SKIP LOCKED</c> alone only excludes rows a concurrent, still-open transaction has
    /// locked — it says nothing about a row a previous claim already committed. The claim UPDATE
    /// therefore also advances <c>NextAttemptAt</c> by <see cref="ClaimLease"/>, so a row stays out
    /// of the claimable set for the lease window even after this statement's own transaction
    /// commits. Without that, a slow send (or a worker that crashes between claiming and
    /// persisting its real outcome) would let the very next tick re-claim and re-send the same row.
    /// </remarks>
    public async Task<IReadOnlyList<NotificationDelivery>> ClaimDueAsync(
        int batchSize, DateTime utcNow, CancellationToken ct = default)
    {
        var leaseUntil = utcNow + ClaimLease;

        var claimed = await _dbSet.FromSqlInterpolated($"""
            UPDATE "NotificationDeliveries"
            SET "NextAttemptAt" = {leaseUntil}
            WHERE "Id" IN (
                SELECT "Id" FROM "NotificationDeliveries"
                WHERE ("State" = 'Pending' OR "State" = 'Failed')
                  AND ("ScheduledFor" IS NULL OR "ScheduledFor" <= {utcNow})
                  AND ("NextAttemptAt" IS NULL OR "NextAttemptAt" <= {utcNow})
                ORDER BY "NextAttemptAt" NULLS FIRST
                LIMIT {batchSize}
                FOR UPDATE SKIP LOCKED
            )
            RETURNING *
            """)
            .ToListAsync(ct);

        return claimed;
    }

    public async Task<NotificationDelivery?> GetByDedupKeyAsync(string dedupKey, CancellationToken ct = default) =>
        await _dbSet.FirstOrDefaultAsync(d => d.DedupKey == dedupKey, ct);

    public async Task<IReadOnlyList<NotificationDelivery>> GetDueForEscalationAsync(
        DateTime utcNow, CancellationToken ct = default)
    {
        // EscalationPolicy's earliest boundary is RepushAfter (120s) — a row younger than that
        // can never take an escalation action yet at any stage, so excluding it here keeps this
        // a bounded index scan instead of every Sent row in the table on every 30s tick.
        var earliestBoundary = utcNow - EscalationPolicy.RepushAfter;

        return await _dbSet
            .Where(d => d.State == Domain.Enums.DeliveryState.Sent
                        && d.SentDate != null
                        && d.SentDate <= earliestBoundary)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<NotificationDelivery>> GetExpiredAsync(
        DateTime utcNow, CancellationToken ct = default) =>
        await _dbSet
            .Where(d => d.ExpiresAt <= utcNow
                        && d.State != Domain.Enums.DeliveryState.Delivered
                        && d.State != Domain.Enums.DeliveryState.DeadLettered
                        && d.State != Domain.Enums.DeliveryState.Undelivered
                        && d.State != Domain.Enums.DeliveryState.Suppressed)
            .ToListAsync(ct);
}
