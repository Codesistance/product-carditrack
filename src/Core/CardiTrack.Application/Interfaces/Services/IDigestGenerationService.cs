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
    /// skipped before any model call. Also invoked at the end of the assessor job so a window
    /// just flagged as a problem rewrites the summary on the same execution, rather than waiting
    /// for the next half-hourly digest schedule.
    /// </summary>
    Task<int> GenerateDueDigestsAsync(DateTime utcNow, CancellationToken ct = default);

    /// <summary>
    /// Writes the account of yesterday for every member whose local day has ended and who has not
    /// been reviewed for it yet; returns how many were written. Cheap to re-run and safe to call on
    /// every pass — a member already reviewed for the date costs one indexed read and no model
    /// call, which is what lets this share the half-hourly digest schedule instead of needing a
    /// schedule of its own for each of the timezones the fleet spans.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="GenerateDueDigestsAsync"/> this is written once and never recomputed: the
    /// day it describes is over, so there is no later reading that could change it.
    /// </remarks>
    Task<int> GenerateDueDaybooksAsync(DateTime utcNow, CancellationToken ct = default);
}
