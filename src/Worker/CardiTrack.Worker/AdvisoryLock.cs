using CardiTrack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CardiTrack.Worker;

/// <summary>
/// A Postgres session-scoped advisory lock, for the once-a-day sweeps where running the same
/// whole-estate scan on all three Cloud Run instances buys nothing but load.
/// </summary>
/// <remarks>
/// <para>
/// Restraint, not correctness. Every job that takes one of these is already idempotent — by
/// fingerprint, by dedup key — so a lost lock costs a repeated scan, never a duplicate outcome.
/// A job that needs several instances dividing work in parallel wants
/// <c>FOR UPDATE SKIP LOCKED</c> claims instead (see <c>NotificationDispatchWorker</c>'s remarks
/// on why it deliberately has no lock at all), and a job that needs exclusivity for correctness
/// wants a claim it can lose safely, not this.
/// </para>
/// <para>
/// Lives in <c>CardiTrack.Worker</c> alongside <c>CronBackgroundService</c> and
/// <c>WorkerOptions</c>, and for the same reason CLAUDE.md gives for those: it is this host's
/// scheduling machinery, not shared infrastructure.
/// </para>
/// </remarks>
internal static class AdvisoryLock
{
    /// <summary>
    /// Runs <paramref name="work"/> under the lock, or returns false without running it when
    /// another instance already holds it.
    /// </summary>
    /// <remarks>
    /// Non-blocking by design (<c>pg_try_advisory_lock</c>, not <c>pg_advisory_lock</c>): an
    /// instance that cannot take the lock skips the run entirely rather than queueing behind it
    /// and doing the identical work a second time the moment it is released.
    /// </remarks>
    /// <param name="key">
    /// Fixed per job. Postgres namespaces advisory locks by the number alone, so two jobs sharing
    /// a key would silently serialize against each other. Taken so far:
    /// <c>8_472_100_001</c> (<c>DataCompletenessWorker</c>) and <c>8_472_100_002</c>
    /// (<c>QuietReassuranceWorker</c>).
    /// </param>
    internal static async Task<bool> TryRunAsync(
        IServiceScopeFactory scopeFactory, long key, Func<Task> work, CancellationToken ct)
    {
        // Its own scope, held open for the whole run: the lock is scoped to the *session*, so it
        // is released the moment this connection closes.
        using var scope = scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync(ct);

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT pg_try_advisory_lock({key});";
            var acquired = (bool?)await command.ExecuteScalarAsync(ct) ?? false;
            if (!acquired)
                return false;
        }

        try
        {
            await work();
        }
        finally
        {
            // CancellationToken.None: a cancelled run still has to hand the lock back, and the
            // token that cancelled the work would cancel this too — leaving the next tick locked
            // out until the connection is reclaimed.
            await using var unlock = connection.CreateCommand();
            unlock.CommandText = $"SELECT pg_advisory_unlock({key});";
            await unlock.ExecuteScalarAsync(CancellationToken.None);
        }

        return true;
    }
}
