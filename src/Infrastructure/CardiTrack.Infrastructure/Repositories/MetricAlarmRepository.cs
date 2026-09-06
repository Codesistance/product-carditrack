using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Entities;
using CardiTrack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CardiTrack.Infrastructure.Repositories;

public class MetricAlarmRepository : Repository<MetricAlarm>, IMetricAlarmRepository
{
    public MetricAlarmRepository(CardiTrackDbContext context) : base(context)
    {
    }

    public async Task<IReadOnlyList<MetricAlarm>> GetByOrganizationAsync(
        Guid organizationId, CancellationToken ct = default) =>
        await _dbSet
            .Where(a => a.OrganizationId == organizationId && a.IsActive)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<MetricAlarm>> GetForMemberAsync(
        Guid organizationId, Guid cardiMemberId, CancellationToken ct = default) =>
        await _dbSet
            .Where(a => a.OrganizationId == organizationId
                        && a.IsActive
                        && (a.CardiMemberId == null || a.CardiMemberId == cardiMemberId))
            .ToListAsync(ct);

    public async Task<MetricAlarm?> GetByIdAsync(
        Guid organizationId, Guid alarmId, CancellationToken ct = default) =>
        await _dbSet.FirstOrDefaultAsync(
            a => a.Id == alarmId && a.OrganizationId == organizationId && a.IsActive, ct);

    public async Task<IReadOnlyList<MetricAlarm>> GetOverridesOfAsync(
        Guid accountAlarmId, CancellationToken ct = default) =>
        await _dbSet
            .Where(a => a.DerivedFromAlarmId == accountAlarmId && a.IsActive)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Guid>> GetOrganizationIdsWithEnabledAlarmsAsync(
        CancellationToken ct = default) =>
        await _dbSet
            .Where(a => a.IsActive && a.IsEnabled)
            .Select(a => a.OrganizationId)
            .Distinct()
            .ToListAsync(ct);
}
