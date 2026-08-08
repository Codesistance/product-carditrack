using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Interfaces.Repositories;

public interface IDeviceConnectionRepository : IRepository<DeviceConnection>
{
    Task<IEnumerable<DeviceConnection>> GetActiveByCardiMemberIdAsync(Guid cardiMemberId);

    /// <summary>
    /// True if any of the given CardiMembers has an active connection. Answers the
    /// onboarding-status existence check in one round trip — that endpoint runs on
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
    Task UpdateLastSyncDateAsync(Guid id, DateTime syncDate);
}
