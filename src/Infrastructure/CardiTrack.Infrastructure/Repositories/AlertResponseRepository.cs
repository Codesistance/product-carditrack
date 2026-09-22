using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Entities;
using CardiTrack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CardiTrack.Infrastructure.Repositories;

public class AlertResponseRepository : Repository<AlertResponse>, IAlertResponseRepository
{
    public AlertResponseRepository(CardiTrackDbContext context) : base(context)
    {
    }

    public async Task<IReadOnlyList<AlertResponse>> GetForAlertAsync(
        Guid alertId, CancellationToken ct = default) =>
        await _dbSet
            .AsNoTracking()
            .Where(r => r.AlertId == alertId)
            .OrderByDescending(r => r.CreatedDate)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<AlertResponse>> GetForAlertsAsync(
        IReadOnlyCollection<Guid> alertIds, CancellationToken ct = default)
    {
        if (alertIds.Count == 0)
            return [];

        return await _dbSet
            .AsNoTracking()
            .Where(r => alertIds.Contains(r.AlertId))
            .OrderByDescending(r => r.CreatedDate)
            .ToListAsync(ct);
    }
}
