using CardiTrack.Application.DTOs.Common;
using CardiTrack.Domain.Enums;

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

    /// <summary>
    /// Writes the account of the calendar month just gone for every member whose local month has
    /// turned and who has not been given one for it yet; returns how many were written. Shares the
    /// same half-hourly schedule as the other books.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Due on the first of the month, once the member's local clock passes their Monthbook time,
    /// and dated by the previous month's last day. A month with fewer than fourteen days of
    /// readings gets none — an unmeasured month cannot be accounted for without speaking for the
    /// weeks that are missing.
    /// </para>
    /// <para>
    /// Composed on the first day of the following month, which keeps the whole month inside every
    /// retention window at the moment it is read. Generated from the month's own measurements,
    /// <b>never</b> from its Weekbooks.
    /// </para>
    /// </remarks>
    Task<int> GenerateDueMonthbooksAsync(DateTime utcNow, CancellationToken ct = default);

    /// <summary>
    /// Composes one CardiJournal book again, at a caregiver's request rather than on the schedule:
    /// the book for <paramref name="audience"/> whose period ends on <paramref name="periodEnd"/>
    /// (the day itself for a Daybook, the week's last day for a Weekbook, the month's last day
    /// for a Monthbook), from that period's own readings, exactly as the due pass would have.
    /// <b>Stores nothing.</b> A <see cref="JournalRewriteOutcome.Written"/> result carries the
    /// entry that passed every guard, for the caller to store through
    /// <see cref="Repositories.IDigestRepository.ReplaceBookAsync"/> inside whatever transaction
    /// the caller's own record of the request needs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The one exception to "written once and never recomputed", and a narrow one: the book is
    /// replaced only when a person asks, and only after the new text has survived every guard
    /// the scheduled write applies. A reply the guards refuse leaves the existing book where it
    /// was — the caregiver asked for a better account, not for none.
    /// </para>
    /// <para>
    /// Composing is split from storing because composing is a model call that can take minutes
    /// and storing is two statements: a caller that needs the store to land with its own writes
    /// can open a transaction around the store alone, rather than holding a connection and a row
    /// lock across the generation.
    /// </para>
    /// <para>
    /// Same stances as the due pass on who is written about: an inactive or paused member gets
    /// <see cref="JournalRewriteOutcome.MemberUnavailable"/>, and a period that has not ended in
    /// the member's own local time is refused rather than written short.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="audience"/> is not a journal book.</exception>
    Task<JournalRewriteResult> ComposeBookAsync(
        Guid cardiMemberId,
        DigestAudience audience,
        DateOnly periodEnd,
        DateTime utcNow,
        CancellationToken ct = default);
}
