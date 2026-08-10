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
}
