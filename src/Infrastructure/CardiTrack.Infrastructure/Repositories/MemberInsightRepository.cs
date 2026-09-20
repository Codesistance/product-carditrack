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

    public async Task<MemberInsight?> GetForAlertAsync(Guid alertId) =>
        await _dbSet.FirstOrDefaultAsync(i => i.AlertId == alertId);

    public async Task<IReadOnlyList<MemberInsight>> GetGeneratedBeforeAsync(DateTime cutoffUtc, int take) =>
        await _dbSet
            .Where(i => i.GeneratedAtUtc < cutoffUtc)
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
