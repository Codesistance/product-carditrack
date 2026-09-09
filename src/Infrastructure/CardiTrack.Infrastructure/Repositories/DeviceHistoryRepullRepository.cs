using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CardiTrack.Infrastructure.Repositories;

public class DeviceHistoryRepullRepository : Repository<DeviceHistoryRepull>, IDeviceHistoryRepullRepository
{
    public DeviceHistoryRepullRepository(CardiTrackDbContext context) : base(context)
    {
    }

    public async Task<DeviceHistoryRepull?> GetOpenByConnectionIdAsync(
        Guid deviceConnectionId, CancellationToken ct = default)
    {
        return await _dbSet
            .AsNoTracking()
            .Where(r => r.DeviceConnectionId == deviceConnectionId && IsOpen(r.Status))
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

        // One query, then the newest per connection picked in memory: a member has a handful of
        // connections and each gains at most a row every couple of days, so the rows here are
        // few, and the (DeviceConnectionId, RequestedAt desc) index serves the read. A DISTINCT
        // ON query is the upgrade if that ever stops being true.
        var rows = await _dbSet
            .AsNoTracking()
            .Where(r => ids.Contains(r.DeviceConnectionId))
            .OrderByDescending(r => r.RequestedAt)
            .ToListAsync(ct);

        return rows
            .GroupBy(r => r.DeviceConnectionId)
            .Select(g => g.First())
            .ToList();
    }

    public async Task<IReadOnlyList<DeviceHistoryRepull>> GetDueAsync(int limit, CancellationToken ct = default)
    {
        return await _dbSet
            .Where(r => IsOpen(r.Status))
            .OrderBy(r => r.RequestedAt)
            .Take(limit)
            .ToListAsync(ct);
    }

    // Written as a comparison on the enum so EF translates it against the stored names; the
    // entity's IsOpen is a computed property and cannot be used in a query.
    private static bool IsOpen(HistoryRepullStatus status) =>
        status == HistoryRepullStatus.Pending || status == HistoryRepullStatus.InProgress;
}
