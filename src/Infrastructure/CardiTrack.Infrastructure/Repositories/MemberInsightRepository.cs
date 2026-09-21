using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CardiTrack.Infrastructure.Repositories;

public class MemberInsightRepository : Repository<MemberInsight>, IMemberInsightRepository
{
    public MemberInsightRepository(CardiTrackDbContext context) : base(context)
    {
    }

    // Deliberately tracked (no AsNoTracking), for the same reason as MemberAdviseRepository: the
    // batch writer reads-then-updates the same row, and the API's read pays nothing measurable for
    // tracking one entity per request.
    public async Task<MemberInsight?> GetByScopeAsync(Guid cardiMemberId, InsightScope scope) =>
        await _dbSet.FirstOrDefaultAsync(i =>
            i.CardiMemberId == cardiMemberId && i.Scope == scope && i.AlertId == null);

    /// <inheritdoc />
    public async Task<DateTime?> GetGeneratedAtUtcAsync(
        Guid cardiMemberId, InsightScope scope, CancellationToken ct = default) =>
        // Projected to a scalar, which is what makes this answer the database rather than the
        // change tracker. The tracked read above would hand back the instance it loaded earlier
        // carrying the timestamp it had then, and the caller is asking precisely whether that is
        // still what is stored.
        await _dbSet
            .Where(i => i.CardiMemberId == cardiMemberId && i.Scope == scope && i.AlertId == null)
            .Select(i => (DateTime?)i.GeneratedAtUtc)
            .FirstOrDefaultAsync(ct);

    /// <inheritdoc />
    public async Task<IReadOnlyList<Guid>> GetMemberIdsWithScopeAsync(
        InsightScope scope, CancellationToken ct = default) =>
        await _dbSet
            .AsNoTracking()
            .Where(i => i.Scope == scope && i.AlertId == null)
            .Select(i => i.CardiMemberId)
            .Distinct()
            .ToListAsync(ct);

    public async Task<MemberInsight?> GetForAlertAsync(Guid alertId) =>
        await _dbSet.FirstOrDefaultAsync(i => i.AlertId == alertId);

    // Member-scoped rows only. An alert explanation describes one fixed past event, the read path
    // serves it however old it is, and sweeping it at ninety days would leave an alert in the
    // caregiver's history that the product declines to explain.
    public async Task<IReadOnlyList<MemberInsight>> GetGeneratedBeforeAsync(DateTime cutoffUtc, int take) =>
        await _dbSet
            .Where(i => i.AlertId == null && i.GeneratedAtUtc < cutoffUtc)
            .OrderBy(i => i.GeneratedAtUtc)
            .Take(take)
            .ToListAsync();

    // Both halves of the predicate, in one server-side statement: the ids the sweep selected *and*
    // the age it selected them for. A row the digest or trend pass refreshed between the two
    // simply stops matching, so the delete leaves it alone instead of discarding a fresh insight.
    public async Task<int> DeleteGeneratedBeforeAsync(
        IReadOnlyCollection<Guid> ids, DateTime cutoffUtc) =>
        ids.Count == 0
            ? 0
            : await _dbSet
                .Where(i => ids.Contains(i.Id) && i.GeneratedAtUtc < cutoffUtc)
                .ExecuteDeleteAsync();
}
