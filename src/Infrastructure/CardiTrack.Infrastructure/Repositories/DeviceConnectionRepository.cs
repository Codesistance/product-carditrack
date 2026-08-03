using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CardiTrack.Infrastructure.Repositories;

public class DeviceConnectionRepository : Repository<DeviceConnection>, IDeviceConnectionRepository
{
    public DeviceConnectionRepository(CardiTrackDbContext context) : base(context)
    {
    }

    public async Task<IEnumerable<DeviceConnection>> GetActiveByCardiMemberIdAsync(Guid cardiMemberId)
    {
        return await _dbSet
            .Where(dc => dc.CardiMemberId == cardiMemberId
                         && dc.IsActive
                         && dc.ConnectionStatus == ConnectionStatus.Connected)
            .ToListAsync();
    }

    public async Task<IEnumerable<DeviceConnection>> GetByCardiMemberIdAsync(Guid cardiMemberId)
    {
        return await _dbSet
            .Where(dc => dc.CardiMemberId == cardiMemberId && dc.IsActive)
            .OrderBy(dc => dc.ConnectedDate)
            .ToListAsync();
    }

    public async Task<IEnumerable<DeviceConnection>> GetDueForSyncAsync(int thresholdMinutes)
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-thresholdMinutes);
        return await _dbSet
            .Where(dc => dc.IsActive
                         && dc.ConnectionStatus == ConnectionStatus.Connected
                         && (dc.LastSyncDate == null || dc.LastSyncDate < cutoff))
            .ToListAsync();
    }

    public async Task UpdateTokenAsync(Guid id, string encryptedAccessToken, string encryptedRefreshToken, DateTime tokenExpiry)
    {
        await _dbSet
            .Where(dc => dc.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(dc => dc.AccessToken, encryptedAccessToken)
                .SetProperty(dc => dc.RefreshToken, encryptedRefreshToken)
                .SetProperty(dc => dc.TokenExpiry, tokenExpiry)
                .SetProperty(dc => dc.ConnectionStatus, ConnectionStatus.Connected));
    }

    public async Task UpdateStatusAsync(Guid id, ConnectionStatus status)
    {
        await _dbSet
            .Where(dc => dc.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(dc => dc.ConnectionStatus, status));
    }

    public async Task UpdateLastSyncDateAsync(Guid id, DateTime syncDate)
    {
        await _dbSet
            .Where(dc => dc.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(dc => dc.LastSyncDate, syncDate));
    }
}
