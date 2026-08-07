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
    Task<IEnumerable<DeviceConnection>> GetDueForSyncAsync(int thresholdMinutes);
    Task UpdateTokenAsync(Guid id, string encryptedAccessToken, string encryptedRefreshToken, DateTime tokenExpiry);
    Task UpdateStatusAsync(Guid id, ConnectionStatus status);
    Task UpdateLastSyncDateAsync(Guid id, DateTime syncDate);
}
