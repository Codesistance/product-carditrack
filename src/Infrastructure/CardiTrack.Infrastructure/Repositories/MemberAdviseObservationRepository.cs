using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Entities;
using CardiTrack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CardiTrack.Infrastructure.Repositories;

public class MemberAdviseObservationRepository
    : Repository<MemberAdviseObservation>, IMemberAdviseObservationRepository
{
    public MemberAdviseObservationRepository(CardiTrackDbContext context) : base(context)
    {
    }

    // AsNoTracking, unlike MemberAdviseRepository: that one is read-then-update by the batch
    // writer, and this one is never updated by anybody. A log read for a report can run to a
    // year of entries, which is the size at which tracking them all would start to cost
    // something for nothing.
    public async Task<IReadOnlyList<MemberAdviseObservation>> GetByCardiMemberAsync(
        Guid cardiMemberId, DateTime fromUtc, DateTime toUtc, int limit, CancellationToken ct = default) =>
        await _dbSet
            .AsNoTracking()
            .Where(o => o.CardiMemberId == cardiMemberId
                && o.ObservedAtUtc >= fromUtc
                && o.ObservedAtUtc < toUtc)
            .OrderByDescending(o => o.ObservedAtUtc)
            .Take(limit)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<MemberAdviseObservation>> GetObservedBeforeAsync(
        DateTime cutoffUtc, int take) =>
        await _dbSet
            .AsNoTracking()
            .Where(o => o.ObservedAtUtc < cutoffUtc)
            .OrderBy(o => o.ObservedAtUtc)
            .Take(take)
            .ToListAsync();

    // The cutoff restated alongside the ids, matching MemberInsightRepository. Entries here are
    // never rewritten, so unlike an insight none can age backwards out of the selection between
    // the two statements — it is kept so both sweeps read the same, and so the predicate stays
    // correct if that ever stops being true.
    public async Task<int> DeleteObservedBeforeAsync(
        IReadOnlyCollection<Guid> ids, DateTime cutoffUtc) =>
        ids.Count == 0
            ? 0
            : await _dbSet
                .Where(o => ids.Contains(o.Id) && o.ObservedAtUtc < cutoffUtc)
                .ExecuteDeleteAsync();
}
