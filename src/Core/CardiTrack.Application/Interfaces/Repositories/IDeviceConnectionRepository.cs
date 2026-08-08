using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Interfaces.Repositories;

public interface IDeviceConnectionRepository : IRepository<DeviceConnection>
{
    Task<IEnumerable<DeviceConnection>> GetActiveByCardiMemberIdAsync(Guid cardiMemberId);

    /// <summary>
    /// True if any of the given CardiMembers still has a device paired — any live connection
    /// that has not been disconnected, whatever state its last sync or token left it in.
    /// Answers the onboarding-status existence check in one round trip — that endpoint runs on
    /// every app launch, so it must not fan out to a query per member.
    /// </summary>
    Task<bool> AnyActiveForCardiMembersAsync(IEnumerable<Guid> cardiMemberIds);

    Task<IEnumerable<DeviceConnection>> GetByCardiMemberIdAsync(Guid cardiMemberId);
    /// <summary>
    /// Connections due a sync, judged against each connection's own SyncFrequencyMinutes.
    /// </summary>
    Task<IEnumerable<DeviceConnection>> GetDueForSyncAsync();

    /// <summary>
    /// A random sample of connections eligible for syncing, for the audit pull. Carries the same
    /// active-and-not-paused filter as <see cref="GetDueForSyncAsync"/> — a paused member's data
    /// must not be collected by any path, and an audit is still collection.
    /// </summary>
    Task<IEnumerable<DeviceConnection>> GetRandomSyncableSampleAsync(int count);
    Task UpdateTokenAsync(Guid id, string encryptedAccessToken, string encryptedRefreshToken, DateTime tokenExpiry);
    Task UpdateStatusAsync(Guid id, ConnectionStatus status);

    /// <summary>
    /// Records a completed pull: stamps the sync date and returns the connection to
    /// <see cref="ConnectionStatus.Connected"/>.
    /// </summary>
    /// <remarks>
    /// The status reset is the point, not a side effect. A pull that fetched a whole window is
    /// proof the connection works, and without writing that back a connection parked in
    /// <see cref="ConnectionStatus.SyncError"/> would keep reporting a fault it had already
    /// recovered from. A connection disconnected while the pull was in flight is left untouched.
    /// </remarks>
    Task MarkSyncSucceededAsync(Guid id, DateTime syncDate);
}
