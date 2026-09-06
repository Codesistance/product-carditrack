using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CardiTrack.Infrastructure.Repositories;

public class ReportRepository : Repository<Report>, IReportRepository
{
    public ReportRepository(CardiTrackDbContext context) : base(context)
    {
    }

    // Tracked: the generation path reads the row it queued and updates it in place.
    public Task<Report?> GetForOwnerAsync(Guid reportId, Guid ownerUserId, CancellationToken ct = default) =>
        _dbSet.FirstOrDefaultAsync(r => r.Id == reportId && r.OwnerUserId == ownerUserId, ct);

    public async Task<IReadOnlyList<Report>> GetExpiredAsync(
        DateTime asOf, int limit, CancellationToken ct = default) =>
        await _dbSet
            .Where(r => r.ExpiresAt <= asOf)
            .OrderBy(r => r.ExpiresAt)
            .Take(limit)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Report>> GetStalePendingAsync(
        DateTime olderThan, int limit, CancellationToken ct = default) =>
        await _dbSet
            .Where(r => r.Status == ReportStatus.Pending && r.CreatedDate <= olderThan)
            .OrderBy(r => r.CreatedDate)
            .Take(limit)
            .ToListAsync(ct);
}
