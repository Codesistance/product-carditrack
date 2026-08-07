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

    public async Task<bool> AnyActiveForCardiMembersAsync(IEnumerable<Guid> cardiMemberIds)
    {
        var ids = cardiMemberIds as IReadOnlyCollection<Guid> ?? cardiMemberIds.ToList();
        if (ids.Count == 0)
            return false;

        return await _dbSet
            .AnyAsync(dc => ids.Contains(dc.CardiMemberId)
                            && dc.IsActive
                            && dc.ConnectionStatus == ConnectionStatus.Connected);
    }

    public async Task<IEnumerable<DeviceConnection>> GetByCardiMemberIdAsync(Guid cardiMemberId)
    {
        return await _dbSet
            .Where(dc => dc.CardiMemberId == cardiMemberId && dc.IsActive)
            .OrderBy(dc => dc.ConnectedDate)
            .ToListAsync();
    }

    /// <summary>
    /// Connections the sync worker should pull next.
    /// <para>
    /// Due-ness is judged against each connection's own SyncFrequencyMinutes rather than a
    /// single global threshold, so a provider warranting a slower cadence is configured per
    /// connection without changing the worker's schedule.
    /// </para>
    /// <para>
    /// Members who are removed or whose monitoring is paused are excluded here rather than in
    /// the worker, so every caller of this query inherits the same rule — pausing monitoring
    /// has to actually stop the data collection, not merely change what the app displays.
    /// </para>
    /// </summary>
    public async Task<IEnumerable<DeviceConnection>> GetDueForSyncAsync()
    {
        var now = DateTime.UtcNow;
        return await _dbSet
            .Where(dc => dc.IsActive
                         && dc.ConnectionStatus == ConnectionStatus.Connected
                         && (dc.LastSyncDate == null
                             || dc.LastSyncDate.Value.AddMinutes(dc.SyncFrequencyMinutes) <= now))
            .Join(_context.CardiMembers, dc => dc.CardiMemberId, cm => cm.Id, (dc, cm) => new { dc, cm })
            .Where(x => x.cm.IsActive
                        && (x.cm.MonitoringPausedUntil == null || x.cm.MonitoringPausedUntil <= now))
            .Select(x => x.dc)
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
