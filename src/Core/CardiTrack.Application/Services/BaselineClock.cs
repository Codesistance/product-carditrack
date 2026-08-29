using CardiTrack.Domain.Common;

namespace CardiTrack.Application.Services;

/// <summary>
/// Clock arithmetic for the two baseline fields that are times of day rather than quantities —
/// typical bedtime and typical wake — and for comparing a night's own times against them.
/// </summary>
/// <remarks>
/// <para>
/// A time of day is a point on a circle, not a number on a line, and both halves of this file
/// exist because that difference is invisible until it produces a wrong answer.
/// <c>BaselineCalculator.TypicalTime</c> already learned it once: it averages bedtimes as angles,
/// because the arithmetic mean of 23:50 and 00:10 is noon.
/// </para>
/// <para>
/// Extracted here so the digest's waking-hours clock and the books' comparison clauses cannot
/// drift. <c>DigestDayProgress</c> carried the only conversion, privately, and a second copy in a
/// prompt builder is exactly the duplication <c>MemberAnchorTimeZone</c> was pulled out to
/// prevent.
/// </para>
/// </remarks>
public static class BaselineClock
{
    /// <summary>
    /// A baseline's UTC time of day, as the member's local wall clock on a given day.
    /// </summary>
    /// <remarks>
    /// Anchored to a date rather than converted with a fixed offset, so a night either side of a
    /// daylight-saving change is read on the clock the member's household actually kept. Null in
    /// stays null; without a zone the stored face is returned unchanged, so fixtures that already
    /// speak in local hours keep working.
    /// </remarks>
    /// <param name="onUtcDate">
    /// The <b>UTC</b> date the stored face is anchored to — it is pinned to that date and read
    /// back on the member's clock, so a local civil date passed here is off by up to a day and
    /// picks the wrong side of a daylight-saving change. Named for the frame rather than left as
    /// "date" because a caller holding both had no way to tell from the signature which one this
    /// wanted, and picked the wrong one.
    /// </param>
    public static TimeOnly? Local(TimeOnly? utcTimeOfDay, DateOnly onUtcDate, TimeZoneInfo? timeZone)
    {
        if (utcTimeOfDay is not { } utcClock)
            return null;
        if (timeZone is null)
            return utcClock;

        var utcInstant = DateTime.SpecifyKind(
            onUtcDate.ToDateTime(TimeOnly.MinValue).Add(utcClock.ToTimeSpan()), DateTimeKind.Utc);
        return TimeOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utcInstant, timeZone));
    }

    /// <summary>
    /// A stored UTC instant as the member's local wall clock. The companion to
    /// <see cref="Local(TimeOnly?, DateOnly, TimeZoneInfo?)"/> for the readings that carry a whole
    /// instant — a night's own falling-asleep and waking times — rather than a learned time of day.
    /// </summary>
    public static TimeOnly? Local(DateTime? utcInstant, TimeZoneInfo? timeZone)
    {
        if (utcInstant is not { } instant)
            return null;
        if (timeZone is null)
            return TimeOnly.FromDateTime(instant);

        return TimeOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(instant, DateTimeKind.Utc), timeZone));
    }

    /// <summary>
    /// How far <paramref name="actual"/> sits from <paramref name="usual"/> in minutes, the short
    /// way round the clock: positive for later, negative for earlier, and never more than
    /// <see cref="JournalComparison.HalfDayMinutes"/> either way.
    /// </summary>
    /// <remarks>
    /// The short way round is the only reading that makes sense of a night: 23:50 against a usual
    /// of 00:10 is twenty minutes early, not twenty-three hours and forty minutes late. It is also
    /// why a direction becomes undecidable at the far end — at exactly half a day the two readings
    /// are the same statement, which is what
    /// <see cref="JournalComparison.DefaultDirectionBoundMinutes"/> stops short of.
    /// </remarks>
    public static int MinutesFrom(TimeOnly actual, TimeOnly usual)
    {
        var raw = (int)Math.Round((actual.ToTimeSpan() - usual.ToTimeSpan()).TotalMinutes);

        // Into [0, 1440) first: C#'s % keeps the sign of the dividend, so a negative span would
        // otherwise stay negative and skip the wrap this whole method exists for.
        var forward = ((raw % JournalComparison.MinutesPerDay) + JournalComparison.MinutesPerDay)
            % JournalComparison.MinutesPerDay;

        // Past half a day forward is nearer going back — the wrap that makes 23:50 early rather
        // than late. The boundary itself stays positive: at exactly 720 the two directions are
        // equally true, and one of them has to be picked for the arithmetic to be total.
        return forward > JournalComparison.HalfDayMinutes
            ? forward - JournalComparison.MinutesPerDay
            : forward;
    }
}
