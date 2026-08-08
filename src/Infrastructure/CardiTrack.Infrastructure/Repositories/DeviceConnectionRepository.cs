using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CardiTrack.Infrastructure.Repositories;

public class DeviceConnectionRepository : Repository<DeviceConnection>, IDeviceConnectionRepository
{
    /// <summary>
    /// Statuses a routine pull may still be attempted against.
    /// </summary>
    /// <remarks>
    /// <see cref="ConnectionStatus.SyncError"/> is in the set because it records a provider API
    /// that failed us, not a connection the user has to fix: excluding it meant one bad response
    /// dropped a healthy device out of the rotation permanently, since the only thing that writes
    /// the status back to <see cref="ConnectionStatus.Connected"/> is a sync that would never be
    /// scheduled again. The retry rides the connection's own SyncFrequencyMinutes, so a failing
    /// provider is polled no harder than a working one.
    /// <para>
    /// <see cref="ConnectionStatus.TokenExpired"/> and <see cref="ConnectionStatus.AuthError"/>
    /// stay out: both mean the provider refused our credentials, and re-consent — not another
    /// pull — is what produces working ones. <see cref="ConnectionStatus.Disconnected"/> stays
    /// out because the user removed the device.
    /// </para>
    /// </remarks>
    private static readonly ConnectionStatus[] SyncableStatuses =
        [ConnectionStatus.Connected, ConnectionStatus.SyncError];

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

    /// <remarks>
    /// The status test is "not disconnected" rather than "connected" on purpose. A device whose
    /// last pull errored, or whose token needs refreshing, is still a paired device — only a
    /// disconnected row means the user no longer has one. Testing for
    /// <see cref="ConnectionStatus.Connected"/> here made a single failed refresh read as no
    /// device at all, which re-opened the add-device wizard over a member who already had one.
    /// This matches how <c>DeviceConnectionService.HasRefreshTokenAsync</c> and the device list
    /// the app shows alongside it both judge the same question.
    /// </remarks>
    public async Task<bool> AnyActiveForCardiMembersAsync(IEnumerable<Guid> cardiMemberIds)
    {
        var ids = cardiMemberIds as IReadOnlyCollection<Guid> ?? cardiMemberIds.ToList();
        if (ids.Count == 0)
            return false;

        return await _dbSet
            .AnyAsync(dc => ids.Contains(dc.CardiMemberId)
                            && dc.IsActive
                            && dc.ConnectionStatus != ConnectionStatus.Disconnected);
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
    /// <para>
    /// See <see cref="SyncableStatuses"/> for why a sync-errored connection is still pulled.
    /// </para>
    /// </summary>
    public async Task<IEnumerable<DeviceConnection>> GetDueForSyncAsync()
    {
        var now = DateTime.UtcNow;
        return await _dbSet
            .Where(dc => dc.IsActive
                         && SyncableStatuses.Contains(dc.ConnectionStatus)
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
    /// <para>
    /// The one deliberate difference is the status: this stays on
    /// <see cref="ConnectionStatus.Connected"/> alone rather than <see cref="SyncableStatuses"/>.
    /// The audit measures how faithfully healthy connections are being pulled, so mixing in
    /// connections that are mid-recovery from a provider failure would move the number it exists
    /// to report without telling us anything about the pull.
    /// </para>
    /// <para>
    /// The randomised order costs a scan of the eligible set plus a bounded top-N sort, which is
    /// the right trade at this size and cadence — the audit runs weekly over a sample of tens.
    /// The index-friendly alternatives buy their speed by returning adjacent rows (an id-pivot
    /// scan, say), and adjacency is exactly the clustering this method exists to avoid: a sample
    /// of neighbours measures one corner of the population no less than a stable ordering does.
    /// Worth revisiting if the eligible set ever reaches the millions, not before.
    /// </para>
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

    public async Task MarkSyncSucceededAsync(Guid id, DateTime syncDate)
    {
        // A connection torn down while its pull was in flight is left exactly as the teardown
        // left it — stamping a sync date, let alone reviving the status, would hand the user
        // back a device they had just removed.
        await _dbSet
            .Where(dc => dc.Id == id
                         && dc.IsActive
                         && dc.ConnectionStatus != ConnectionStatus.Disconnected)
            .ExecuteUpdateAsync(s => s
                .SetProperty(dc => dc.LastSyncDate, syncDate)
                .SetProperty(dc => dc.ConnectionStatus, ConnectionStatus.Connected));
    }
}
