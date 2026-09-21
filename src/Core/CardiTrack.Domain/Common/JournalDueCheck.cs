using CardiTrack.Domain.Entities;

namespace CardiTrack.Domain.Common;

/// <summary>
/// When a member's weekly and monthly CardiJournal work falls due, and which period it covers.
/// One source of truth for every generator that runs on the half-hourly digest pass.
/// </summary>
/// <remarks>
/// <para>
/// Extracted from <c>DigestGenerationService</c> when trend interpretation arrived as a second
/// caller. The rule is per-member and timezone-sensitive — a member chooses the weekday their
/// journal week starts and the local hour each book is written at — so two copies of it would
/// drift, and the drift would not be a crash: it would be a trend note dated to a different week
/// than the Weekbook sitting beside it, each describing seven days the other did not.
/// </para>
/// <para>
/// Lives in Domain beside <see cref="JournalSchedule"/>, whose defaults it reads, and takes a
/// <see cref="CardiMember"/> and an already-resolved local clock rather than resolving the
/// timezone itself: resolving the anchor is a repository read, and a due check that could not be
/// exercised without one would be a rule nobody could test at the boundaries that matter.
/// </para>
/// </remarks>
public static class JournalDueCheck
{
    /// <summary>
    /// The seven days a weekly read covers, or null when the member is not due at this local
    /// instant. Due on the day their week starts, once their chosen hour has passed; the period
    /// is the week that ended last night, dated by its last day.
    /// </summary>
    public static JournalPeriod? Weekly(CardiMember member, DateTime localNow)
    {
        ArgumentNullException.ThrowIfNull(member);

        var localToday = DateOnly.FromDateTime(localNow);
        if (localToday.DayOfWeek != JournalSchedule.EffectiveWeekStart(member.JournalWeekStartsOn))
            return null;

        if (TimeOnly.FromDateTime(localNow) < JournalSchedule.EffectiveTime(member.WeekbookLocalTime))
            return null;

        var end = localToday.AddDays(-1);
        return new JournalPeriod(end.AddDays(-6), end);
    }

    /// <summary>
    /// The calendar month a monthly read covers, or null when the member is not due at this local
    /// instant. Due on the first, once their chosen hour has passed; the period is the month just
    /// gone, dated by its last day.
    /// </summary>
    public static JournalPeriod? Monthly(CardiMember member, DateTime localNow)
    {
        ArgumentNullException.ThrowIfNull(member);

        var localToday = DateOnly.FromDateTime(localNow);
        if (localToday.Day != 1)
            return null;

        if (TimeOnly.FromDateTime(localNow) < JournalSchedule.EffectiveTime(member.MonthbookLocalTime))
            return null;

        var end = localToday.AddDays(-1);
        return new JournalPeriod(new DateOnly(end.Year, end.Month, 1), end);
    }

    /// <summary>
    /// Whether any timezone on earth could put a member's local calendar on
    /// <paramref name="dayOfMonth"/> at this instant.
    /// </summary>
    /// <remarks>
    /// Real UTC offsets run from -12:00 to +14:00, so the fleet's local clocks span 26 hours and
    /// touch at most three calendar dates at once. Deliberately generous at both ends rather than
    /// enumerating the timezone database: being wrong towards "possible" costs one pass that
    /// declines every member individually, while being wrong towards "impossible" would lose a
    /// member their book for good.
    /// </remarks>
    public static bool AnyTimeZoneCouldBeOnDayOfMonth(DateTime utcNow, int dayOfMonth)
    {
        var earliest = DateOnly.FromDateTime(utcNow.AddHours(-12));
        var latest = DateOnly.FromDateTime(utcNow.AddHours(14));

        for (var date = earliest; date <= latest; date = date.AddDays(1))
        {
            if (date.Day == dayOfMonth)
                return true;
        }

        return false;
    }
}

/// <summary>One finished period of a member's own local calendar, inclusive at both ends.</summary>
/// <param name="Start">First day covered.</param>
/// <param name="End">Last day covered — the date the period is identified by.</param>
public readonly record struct JournalPeriod(DateOnly Start, DateOnly End)
{
    /// <summary>How many days the period spans, both ends included.</summary>
    public int DayCount => End.DayNumber - Start.DayNumber + 1;
}
