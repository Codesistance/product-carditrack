using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Interfaces.Repositories;

public interface IMemberInsightRepository : IRepository<MemberInsight>
{
    /// <summary>
    /// The member's current insight at one scope, or null when none has been written. Tracked,
    /// because the one writer (the batch regeneration) reads then updates the same row.
    /// </summary>
    Task<MemberInsight?> GetByScopeAsync(Guid cardiMemberId, InsightScope scope);

    /// <summary>The explanation written for one alert, or null when the pass has not reached it.</summary>
    Task<MemberInsight?> GetForAlertAsync(Guid alertId);

    /// <summary>
    /// Rows written before <paramref name="cutoffUtc"/>, for the retention sweep. Capped by
    /// <paramref name="take"/> so one pass cannot load an unbounded set into memory.
    /// </summary>
    /// <remarks>
    /// Alert-scoped rows are excluded. <see cref="CardiTrack.Application.Services.InsightServability"/>
    /// promises that an explanation of one past event never goes stale, and a sweep that removed
    /// them at ninety days would quietly break that promise for exactly the caregiver who opens an
    /// old alert from their history. They go with the alert itself, on erasure.
    /// </remarks>
    Task<IReadOnlyList<MemberInsight>> GetGeneratedBeforeAsync(DateTime cutoffUtc, int take);

    /// <summary>
    /// Deletes the named rows, but only those still written before <paramref name="cutoffUtc"/>.
    /// Returns how many were removed.
    /// </summary>
    /// <remarks>
    /// The cutoff is repeated on the delete rather than trusted from the select. A row is selected
    /// for its age and removed by its key, and between those two statements the digest or trend
    /// pass can rewrite that same row in place — at which point deleting by key alone would throw
    /// away an insight generated seconds ago. Re-stating the predicate makes the database decide,
    /// so a row that has just been refreshed is left where it is.
    /// </remarks>
    Task<int> DeleteGeneratedBeforeAsync(IReadOnlyCollection<Guid> ids, DateTime cutoffUtc);
}
