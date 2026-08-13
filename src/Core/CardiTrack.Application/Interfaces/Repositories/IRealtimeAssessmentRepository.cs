using CardiTrack.Domain.Entities;

namespace CardiTrack.Application.Interfaces.Repositories;

/// <summary>
/// Access to the day-partitioned real-time assessment table. Writes are upserts on the natural
/// key (CardiMemberId, WindowStartUtc) — re-running a job execution must be able to overwrite,
/// never duplicate.
/// </summary>
public interface IRealtimeAssessmentRepository
{
    /// <summary>
    /// Writes the assessment; true when this call inserted the row, false when it overwrote an
    /// existing one. The distinction is the concurrency arbiter for severity routing: two
    /// overlapping passes can assess the same window, but only one of them inserts — and only
    /// the inserter may raise the alert.
    /// </summary>
    Task<bool> UpsertAsync(RealtimeAssessment assessment, CancellationToken ct = default);

    /// <summary>Whether an assessment already exists for this exact window — the dedup probe
    /// that keeps an unchanged window from ever reaching the model twice.</summary>
    Task<bool> ExistsAsync(Guid cardiMemberId, DateTime windowStartUtc, CancellationToken ct = default);

    /// <summary>The member's most recent assessment by window start, or null.</summary>
    Task<RealtimeAssessment?> GetLatestAsync(Guid cardiMemberId, CancellationToken ct = default);

    /// <summary>
    /// The member's assessments whose window starts at or after <paramref name="sinceUtc"/>, newest
    /// first — what the assessor has noticed over a stretch, rather than only its last word on the
    /// member.
    /// </summary>
    /// <remarks>
    /// The filter is on the partition column, so a short window reads a small number of partitions
    /// rather than the whole table.
    /// </remarks>
    Task<IReadOnlyList<RealtimeAssessment>> GetSinceAsync(
        Guid cardiMemberId, DateTime sinceUtc, CancellationToken ct = default);
}
