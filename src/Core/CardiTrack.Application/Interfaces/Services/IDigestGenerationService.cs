namespace CardiTrack.Application.Interfaces.Services;

/// <summary>
/// Generates the daily family digests that are due at this moment — "due" meaning a member's
/// anchor timezone has just entered its 06:00 delivery hour and no digest exists yet for that
/// local day. Driven hourly by the pipeline's Cloud Run job (the AI pipeline's sanctioned home
/// per CLAUDE.md — digests are LLM work and must not run in the Worker).
/// </summary>
public interface IDigestGenerationService
{
    /// <summary>
    /// Generates every digest due at <paramref name="utcNow"/>; returns how many were written.
    /// Idempotent — a re-run within the same hour regenerates nothing.
    /// </summary>
    Task<int> GenerateDueDigestsAsync(DateTime utcNow, CancellationToken ct = default);
}
