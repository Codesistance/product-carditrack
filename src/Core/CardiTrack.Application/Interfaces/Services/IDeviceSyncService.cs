using CardiTrack.Domain.Entities;

namespace CardiTrack.Application.Interfaces.Services;

public interface IDeviceSyncService
{
    /// <summary>
    /// Pulls the routine window for one connection. At <see cref="SyncScope.WorkerCadence"/>, each
    /// routine-window day also fetches its granular (minute-grain) series, and a successful pull
    /// is followed by one history-backfill chunk — the Worker's cadence opts in so a fresh
    /// connection's history and granular substrate fill on schedule, while the user-facing manual
    /// sync stays at <see cref="SyncScope.Routine"/>: a caregiver waiting on a refresh must not
    /// pay for ninety days of history or four extra series.
    /// </summary>
    Task SyncCardiMemberAsync(DeviceConnection connection, SyncScope scope = SyncScope.Routine);

    /// <summary>
    /// Re-fetches a deliberately wider window than the routine sync, to see how far back the
    /// provider still revises data. Run over a small sample rather than every connection, since the
    /// point is to measure the revision tail, not to pay for it on every pull.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="SyncCardiMemberAsync"/> this stamps no LastSyncDate and makes no
    /// SyncError transition: an audit is an observation, so it must neither advance a connection's
    /// schedule nor take a healthy connection out of service when a historical day fails to come
    /// back. It is not status-free, though — the token refresh it goes through still marks a
    /// connection TokenExpired when refreshing fails, and deliberately so: that is a genuinely
    /// broken connection rather than an artefact of the wider window.
    /// </remarks>
    Task AuditSyncAsync(DeviceConnection connection);

    /// <summary>
    /// Re-reads an explicit stretch of history for one connection — every day from
    /// <paramref name="to"/> back to <paramref name="from"/>, inclusive, newest first — storing
    /// the daily snapshot and the granular series of each day the provider has data for, and
    /// returning how many days that was. The engine behind a caregiver's history re-pull;
    /// <c>HistoryRepullWorker</c> chooses the chunks and is the only caller.
    /// </summary>
    /// <remarks>
    /// Same contract as <see cref="AuditSyncAsync"/>: no <c>LastSyncDate</c> stamp, no
    /// <c>HistoryBackfilledTo</c> advance, no <c>SyncError</c> transition. A day the provider
    /// refuses sixty days back says nothing about whether the connection works today, and
    /// parking a working device in SyncError over a re-pull would misreport it on the very
    /// screen the caregiver asked from. The token refresh can still mark it <c>TokenExpired</c>,
    /// deliberately — that is a broken connection. Provider failures propagate to the caller,
    /// which decides whether to retry the chunk.
    /// </remarks>
    Task<int> PullHistoryRangeAsync(
        DeviceConnection connection, DateOnly from, DateOnly to, CancellationToken ct = default);
}
