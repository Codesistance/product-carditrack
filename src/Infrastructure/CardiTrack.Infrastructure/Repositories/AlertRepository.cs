using CardiTrack.Application.DTOs.Common;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CardiTrack.Infrastructure.Repositories;

public class AlertRepository : Repository<Alert>, IAlertRepository
{
    public AlertRepository(CardiTrackDbContext context) : base(context)
    {
    }

    public async Task<IEnumerable<Alert>> GetByCardiMemberAsync(Guid cardiMemberId, bool activeOnly)
    {
        var query = _dbSet.Where(a => a.CardiMemberId == cardiMemberId);

        if (activeOnly)
            query = query.Where(a => a.IsActive);

        // Tracked on purpose: AlertResolution mutates these entities and SaveChanges.
        return await query.OrderByDescending(a => a.TriggeredDate).ToListAsync();
    }

    public async Task<IReadOnlyList<Alert>> GetUnresolvedByCardiMemberAsync(Guid cardiMemberId)
    {
        return await _dbSet.AsNoTracking()
            .Where(a => a.CardiMemberId == cardiMemberId && a.IsActive && !a.IsResolved)
            .OrderByDescending(a => a.TriggeredDate)
            // Ties are real: the pipeline can emit several alerts for one member in the same
            // instant, and the dashboard takes the head of this list — an unstable sort would
            // swap which five it shows between refreshes.
            .ThenByDescending(a => a.Id)
            .ToListAsync();
    }

    public async Task<DateTime?> GetLastTriggeredDateAsync(
        Guid cardiMemberId, CancellationToken ct = default)
    {
        // No IsActive / IsResolved filter — see the interface for why this one read counts rows
        // the lists have finished with. MaxAsync over a nullable projection rather than
        // OrderByDescending().FirstOrDefault(): it is one aggregate, and it returns null for a
        // member with no alerts instead of throwing on an empty sequence.
        return await _dbSet.AsNoTracking()
            .Where(a => a.CardiMemberId == cardiMemberId)
            .MaxAsync(a => (DateTime?)a.TriggeredDate, ct);
    }

    public async Task<Alert?> GetByIdWithCardiMemberAsync(Guid alertId)
    {
        return await _dbSet.FirstOrDefaultAsync(a => a.Id == alertId);
    }

    public async Task<IReadOnlyList<Alert>> QueryAsync(AlertQuery query, CancellationToken ct = default)
    {
        if (query.CardiMemberIds.Count == 0)
            return [];

        return await Filtered(query)
            .AsNoTracking()
            .OrderByDescending(a => a.TriggeredDate)
            // Ties are real: the pipeline can emit several alerts for one member in the same
            // instant, and an unstable sort would let paging repeat or skip them.
            .ThenByDescending(a => a.Id)
            .Skip(query.Offset)
            .Take(query.Limit)
            .ToListAsync(ct);
    }

    public async Task<int> CountAsync(AlertQuery query, CancellationToken ct = default)
    {
        if (query.CardiMemberIds.Count == 0)
            return 0;

        return await Filtered(query).CountAsync(ct);
    }

    public async Task<int> CountUnreadAsync(
        IReadOnlyCollection<Guid> cardiMemberIds, CancellationToken ct = default)
    {
        if (cardiMemberIds.Count == 0)
            return 0;

        var ids = cardiMemberIds.ToList();
        return await _dbSet
            .Where(a => a.IsActive && ids.Contains(a.CardiMemberId))
            .Where(a => !a.IsResolved && a.AcknowledgedDate == null)
            .CountAsync(ct);
    }

    /// <summary>
    /// The filter half of <see cref="QueryAsync"/>, shared with <see cref="CountAsync"/> so a
    /// page and its total can never be computed from different predicates.
    /// </summary>
    private IQueryable<Alert> Filtered(AlertQuery query)
    {
        var ids = query.CardiMemberIds.ToList();
        var alerts = _dbSet.Where(a => a.IsActive && ids.Contains(a.CardiMemberId));

        if (query.Severity is { } severity)
            alerts = alerts.Where(a => a.Severity == severity);

        alerts = query.Status switch
        {
            AlertStatus.New => alerts.Where(a => !a.IsResolved && a.AcknowledgedDate == null),
            AlertStatus.Acknowledged => alerts.Where(a => !a.IsResolved && a.AcknowledgedDate != null),
            AlertStatus.Resolved => alerts.Where(a => a.IsResolved),
            _ => alerts,
        };

        if (query.From is { } from)
            alerts = alerts.Where(a => a.TriggeredDate >= from);

        if (query.To is { } to)
            alerts = alerts.Where(a => a.TriggeredDate <= to);

        return alerts;
    }
}
