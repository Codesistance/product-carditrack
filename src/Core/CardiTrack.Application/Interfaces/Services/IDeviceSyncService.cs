using CardiTrack.Domain.Entities;

namespace CardiTrack.Application.Interfaces.Services;

public interface IDeviceSyncService
{
    Task SyncCardiMemberAsync(DeviceConnection connection);

    /// <summary>
    /// Re-fetches a deliberately wider window than the routine sync, to see how far back the
    /// provider still revises data. Run over a small sample rather than every connection, since the
    /// point is to measure the revision tail, not to pay for it on every pull.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="SyncCardiMemberAsync"/> this leaves LastSyncDate and ConnectionStatus
    /// alone: an audit is an observation, so it must neither advance a connection's schedule nor
    /// take a healthy connection out of service when a historical day fails to come back.
    /// </remarks>
    Task AuditSyncAsync(DeviceConnection connection);
}
