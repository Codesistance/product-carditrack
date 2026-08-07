using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Entities;
using CardiTrack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CardiTrack.Infrastructure.Repositories;

/// <inheritdoc cref="IAuditLogRepository"/>
public class AuditLogRepository : IAuditLogRepository
{
    private readonly IDbContextFactory<CardiTrackDbContext> _contextFactory;

    public AuditLogRepository(IDbContextFactory<CardiTrackDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    /// <summary>
    /// Writes through its own short-lived context, not the request-scoped one.
    /// </summary>
    /// <remarks>
    /// Two reasons, and the second is the one that bites. An audit entry must survive the thing
    /// it describes — if the request's own work rolls back, the fact that someone looked at a
    /// wearer's data is still true. And calling SaveChanges on the shared context would flush
    /// whatever else the request happened to have tracked, committing unrelated half-finished
    /// work as a side effect of writing an audit row. A separate context makes the write
    /// genuinely independent rather than merely appearing to be.
    /// </remarks>
    public async Task AppendAsync(AuditLog entry, CancellationToken ct = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(ct);
        await context.Set<AuditLog>().AddAsync(entry, ct);
        await context.SaveChangesAsync(ct);
    }

    public Task<IReadOnlyList<AuditLog>> GetByCardiMemberAsync(
        Guid cardiMemberId, DateTime from, DateTime to, CancellationToken ct = default) =>
        QueryAsync(a => a.CardiMemberId == cardiMemberId, from, to, ct);

    public Task<IReadOnlyList<AuditLog>> GetByUserAsync(
        Guid userId, DateTime from, DateTime to, CancellationToken ct = default) =>
        QueryAsync(a => a.UserId == userId, from, to, ct);

    /// <summary>Reads are diagnostic and never write back, so they skip change tracking.</summary>
    private async Task<IReadOnlyList<AuditLog>> QueryAsync(
        System.Linq.Expressions.Expression<Func<AuditLog, bool>> predicate,
        DateTime from, DateTime to, CancellationToken ct)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(ct);
        return await context.Set<AuditLog>()
            .AsNoTracking()
            .Where(predicate)
            .Where(a => a.Timestamp >= from && a.Timestamp <= to)
            .OrderByDescending(a => a.Timestamp)
            .ToListAsync(ct);
    }
}
