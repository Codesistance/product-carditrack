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

    /// <summary>
    /// A random sample of syncable connections, for the audit pull.
    /// </summary>
    /// <remarks>
    /// Randomised rather than ordered, because any stable ordering would audit the same
    /// connections every week and measure one corner of the population forever. The eligibility
    /// filter is deliberately identical to <see cref="GetDueForSyncAsync"/> minus due-ness: an
    /// audit still fetches a member's health data, so a paused or removed member must be excluded
    /// exactly as they are from a routine pull.
    /// </remarks>
    public async Task<IEnumerable<DeviceConnection>> GetRandomSyncableSampleAsync(int count)
    {
        if (count <= 0)
            return [];

        var now = DateTime.UtcNow;
        return await _dbSet
            .Where(dc => dc.IsActive && dc.ConnectionStatus == ConnectionStatus.Connected)
            .Join(_context.CardiMembers, dc => dc.CardiMemberId, cm => cm.Id, (dc, cm) => new { dc, cm })
            .Where(x => x.cm.IsActive
                        && (x.cm.MonitoringPausedUntil == null || x.cm.MonitoringPausedUntil <= now))
            .Select(x => x.dc)
            .OrderBy(_ => EF.Functions.Random())
            .Take(count)
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
