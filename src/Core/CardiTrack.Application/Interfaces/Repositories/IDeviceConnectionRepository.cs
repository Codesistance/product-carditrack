using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Interfaces.Repositories;

public interface IDeviceConnectionRepository : IRepository<DeviceConnection>
{
    Task<IEnumerable<DeviceConnection>> GetActiveByCardiMemberIdAsync(Guid cardiMemberId);
    Task<IEnumerable<DeviceConnection>> GetByCardiMemberIdAsync(Guid cardiMemberId);
    Task<IEnumerable<DeviceConnection>> GetDueForSyncAsync(int thresholdMinutes);
    Task UpdateTokenAsync(Guid id, string encryptedAccessToken, string encryptedRefreshToken, DateTime tokenExpiry);
    Task UpdateStatusAsync(Guid id, ConnectionStatus status);
    Task UpdateLastSyncDateAsync(Guid id, DateTime syncDate);
}
