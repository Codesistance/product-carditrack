using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Entities;
using CardiTrack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CardiTrack.Infrastructure.Repositories;

public class MetricAlarmStateRepository : Repository<MetricAlarmState>, IMetricAlarmStateRepository
{
    public MetricAlarmStateRepository(CardiTrackDbContext context) : base(context)
    {
    }

    public async Task<IReadOnlyList<MetricAlarmState>> GetByCardiMemberAsync(
        Guid cardiMemberId, CancellationToken ct = default) =>
        await _dbSet.Where(s => s.CardiMemberId == cardiMemberId).ToListAsync(ct);

    public async Task DeleteForAlarmAsync(Guid metricAlarmId, CancellationToken ct = default)
    {
        var rows = await _dbSet.Where(s => s.MetricAlarmId == metricAlarmId).ToListAsync(ct);
        if (rows.Count > 0)
            _dbSet.RemoveRange(rows);
    }
}
