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
    Task<IReadOnlyList<MemberInsight>> GetGeneratedBeforeAsync(DateTime cutoffUtc, int take);
}
