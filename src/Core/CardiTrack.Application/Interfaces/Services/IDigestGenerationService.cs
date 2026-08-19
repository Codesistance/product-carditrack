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

    /// <summary>
    /// Writes the account of the week just gone for every member whose journal week has turned and
    /// who has not been given one for it yet; returns how many were written. Shares the same
    /// half-hourly schedule and the same cheap existence probe as the Daybook pass.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Due on the member's own week-start day, once their local clock passes their Weekbook time,
    /// and written once for the seven days ending the evening before. A week with fewer than four
    /// days of readings gets none: an account of an unmeasured week would have to speak for the
    /// days that are missing, and silence must never read as healthy.
    /// </para>
    /// <para>
    /// Generated from the week's own measurements, <b>never</b> from its Daybooks — so no
    /// imprecision propagates upward, and a week whose Daybooks were skipped or discarded still
    /// gets its Weekbook.
    /// </para>
    /// </remarks>
    Task<int> GenerateDueWeekbooksAsync(DateTime utcNow, CancellationToken ct = default);
}
