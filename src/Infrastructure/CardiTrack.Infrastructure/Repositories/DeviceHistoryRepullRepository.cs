using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CardiTrack.Infrastructure.Repositories;

public class DeviceHistoryRepullRepository : Repository<DeviceHistoryRepull>, IDeviceHistoryRepullRepository
{
    /// <summary>
    /// The statuses that count as open — the Worker still has work to do for the request.
    /// </summary>
    /// <remarks>
    /// An array with <c>Contains</c>, the same shape <c>DeviceConnectionRepository</c> uses for
    /// its syncable statuses, because it translates to a SQL <c>IN</c>. A helper method reads
    /// better and does not translate at all: EF cannot see inside it, so the query throws at
    /// runtime rather than failing to compile. This list and the partial indexes' filters in
    /// <c>DeviceHistoryRepullConfiguration</c> must name the same statuses.
    /// </remarks>
    private static readonly HistoryRepullStatus[] OpenStatuses =
        [HistoryRepullStatus.Pending, HistoryRepullStatus.InProgress];

    public DeviceHistoryRepullRepository(CardiTrackDbContext context) : base(context)
    {
    }

    public async Task<DeviceHistoryRepull?> GetOpenByConnectionIdAsync(
        Guid deviceConnectionId, CancellationToken ct = default)
    {
        return await _dbSet
            .AsNoTracking()
            .Where(r => r.DeviceConnectionId == deviceConnectionId && OpenStatuses.Contains(r.Status))
            .OrderByDescending(r => r.RequestedAt)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<DateTime?> GetLastCompletedAtAsync(Guid deviceConnectionId, CancellationToken ct = default)
    {
        return await _dbSet
            .AsNoTracking()
            .Where(r => r.DeviceConnectionId == deviceConnectionId
                        && r.Status == HistoryRepullStatus.Completed
                        && r.CompletedAt != null)
            .MaxAsync(r => r.CompletedAt, ct);
    }

    public async Task<IReadOnlyList<DeviceHistoryRepull>> GetLatestByConnectionIdsAsync(
        IEnumerable<Guid> deviceConnectionIds, CancellationToken ct = default)
    {
        var ids = deviceConnectionIds.Distinct().ToList();
        if (ids.Count == 0)
            return [];

        // One row per connection, chosen in the database rather than by loading the history and
        // picking in memory. The device list is read on every visit to the screen and on every
        // app resume, and these rows accumulate for the life of the connection, so a read that
        // scales with how many re-pulls a member has ever asked for would get slower for exactly
        // the caregivers who use the feature. The (DeviceConnectionId, RequestedAt desc) index
        // serves this directly.
        return await _dbSet
            .AsNoTracking()
            .Where(r => ids.Contains(r.DeviceConnectionId))
            .GroupBy(r => r.DeviceConnectionId)
            .Select(g => g.OrderByDescending(r => r.RequestedAt).First())
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<DeviceHistoryRepull>> GetDueAsync(int limit, CancellationToken ct = default)
    {
        return await _dbSet
            .Where(r => OpenStatuses.Contains(r.Status))
            .OrderBy(r => r.RequestedAt)
            .Take(limit)
            .ToListAsync(ct);
    }
}
