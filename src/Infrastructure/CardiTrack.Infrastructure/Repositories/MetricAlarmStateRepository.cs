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

    /// <summary>
    /// Server-side, like the other bulk deletes in this folder: an account-level alarm has one state
    /// row per inheriting member, and loading them all just to mark them deleted is a read the
    /// database can do without. Executes immediately rather than at <c>SaveChangesAsync</c>.
    /// </summary>
    public async Task DeleteForAlarmAsync(Guid metricAlarmId, CancellationToken ct = default) =>
        await _dbSet.Where(s => s.MetricAlarmId == metricAlarmId).ExecuteDeleteAsync(ct);
}
