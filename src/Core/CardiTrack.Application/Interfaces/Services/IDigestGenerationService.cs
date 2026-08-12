namespace CardiTrack.Application.Interfaces.Services;

/// <summary>
/// Generates the family summaries that are due at this moment — "due" meaning a member's data has
/// moved since their last summary was written, so what a family reads reflects the readings the
/// service actually holds. Driven by the pipeline's Cloud Run job (the AI pipeline's sanctioned
/// home per CLAUDE.md — summaries are LLM work and must not run in the Worker).
/// </summary>
public interface IDigestGenerationService
{
    /// <summary>
    /// Generates every summary due at <paramref name="utcNow"/>; returns how many were written.
    /// Cheap to re-run — a member whose readings have not changed since their last summary is
    /// skipped before any model call.
    /// </summary>
    Task<int> GenerateDueDigestsAsync(DateTime utcNow, CancellationToken ct = default);
}
