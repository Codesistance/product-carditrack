using CardiTrack.Domain.Entities;

namespace CardiTrack.Application.Interfaces.Repositories;

/// <summary>
/// Access to the day-partitioned real-time assessment table. Writes are upserts on the natural
/// key (CardiMemberId, WindowStartUtc) — re-running a job execution must be able to overwrite,
/// never duplicate.
/// </summary>
public interface IRealtimeAssessmentRepository
{
    Task UpsertAsync(RealtimeAssessment assessment, CancellationToken ct = default);

    /// <summary>Whether an assessment already exists for this exact window — the dedup probe
    /// that keeps an unchanged window from ever reaching the model twice.</summary>
    Task<bool> ExistsAsync(Guid cardiMemberId, DateTime windowStartUtc, CancellationToken ct = default);

    /// <summary>The member's most recent assessment by window start, or null.</summary>
    Task<RealtimeAssessment?> GetLatestAsync(Guid cardiMemberId, CancellationToken ct = default);
}
