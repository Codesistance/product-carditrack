using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Entities;
using CardiTrack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CardiTrack.Infrastructure.Repositories;

/// <inheritdoc cref="IAuditLogRepository"/>
public class AuditLogRepository : IAuditLogRepository
{
    private readonly CardiTrackDbContext _context;

    public AuditLogRepository(CardiTrackDbContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Saves immediately rather than joining the request's unit of work. An audit entry must
    /// survive the thing it describes: if the request's own SaveChanges rolls back, the record
    /// that someone looked at a wearer's data is still true and still needs keeping.
    /// </summary>
    public async Task AppendAsync(AuditLog entry, CancellationToken ct = default)
    {
        await _context.Set<AuditLog>().AddAsync(entry, ct);
        await _context.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<AuditLog>> GetByCardiMemberAsync(
        Guid cardiMemberId, DateTime from, DateTime to, CancellationToken ct = default) =>
        await Query(a => a.CardiMemberId == cardiMemberId, from, to).ToListAsync(ct);

    public async Task<IReadOnlyList<AuditLog>> GetByUserAsync(
        Guid userId, DateTime from, DateTime to, CancellationToken ct = default) =>
        await Query(a => a.UserId == userId, from, to).ToListAsync(ct);

    /// <summary>Reads are diagnostic and never write back, so they skip change tracking.</summary>
    private IQueryable<AuditLog> Query(
        System.Linq.Expressions.Expression<Func<AuditLog, bool>> predicate, DateTime from, DateTime to) =>
        _context.Set<AuditLog>()
            .AsNoTracking()
            .Where(predicate)
            .Where(a => a.Timestamp >= from && a.Timestamp <= to)
            .OrderByDescending(a => a.Timestamp);
}
