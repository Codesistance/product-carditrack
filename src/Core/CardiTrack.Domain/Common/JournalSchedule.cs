namespace CardiTrack.Domain.Common;

/// <summary>
/// When each CardiJournal book is written, and the bounds a caregiver may move that to.
/// One source of truth for the generator, the API validator and the app, so a time the app
/// offers can never be one the generator refuses to act on.
/// </summary>
public static class JournalSchedule
{
    /// <summary>
    /// The default local time every book is written at. Not midnight: a watch syncs on its own
    /// schedule and the last hours of a day routinely arrive after it, so a book written at 00:01
    /// would describe a period whose evening had not landed yet — and unlike the live summary a
    /// book is written once and never revisited, so what it misses it misses for good.
    /// </summary>
    public static readonly TimeOnly DefaultLocalTime = new(2, 0);

    /// <summary>The weekday a CardiJournal week begins when the caregiver has not chosen one.</summary>
    public const DayOfWeek DefaultWeekStartsOn = DayOfWeek.Monday;

    /// <summary>
    /// Earliest a caregiver may move a book to. Before this the tail of the period is still
    /// arriving, and a book cannot be rewritten once it is wrong.
    /// </summary>
    public static readonly TimeOnly EarliestLocalTime = new(1, 0);

    /// <summary>
    /// Latest a caregiver may move a book to. An account of yesterday that arrives mid-afternoon
    /// has stopped being something anyone can act on.
    /// </summary>
    public static readonly TimeOnly LatestLocalTime = new(12, 0);

    /// <summary>
    /// The granularity a chosen time must land on. The digest job runs every half hour, so a
    /// book asked for at 02:17 would in fact be written at 02:30 — offering a minute the
    /// generator cannot honour would be a setting that quietly lies about itself.
    /// </summary>
    public const int StepMinutes = 30;

    /// <summary>
    /// Whether a caregiver-chosen time is one the generator can actually honour: inside the
    /// window, and on a half-hour boundary. Null is always valid and means
    /// <see cref="DefaultLocalTime"/>.
    /// </summary>
    public static bool IsSelectableTime(TimeOnly? time)
    {
        if (time is null)
            return true;

        var value = time.Value;
        if (value < EarliestLocalTime || value > LatestLocalTime)
            return false;

        return value.Second == 0
            && value.Millisecond == 0
            && value.Minute % StepMinutes == 0;
    }

    /// <summary>The stored time, or the default when none has been chosen.</summary>
    /// <remarks>
    /// Named apart from <see cref="EffectiveWeekStart"/> rather than overloaded: two overloads
    /// taking different nullable value types make a bare <c>null</c> argument ambiguous at every
    /// call site, which is precisely the argument a caller with no stored choice has to pass.
    /// </remarks>
    public static TimeOnly EffectiveTime(TimeOnly? chosen) => chosen ?? DefaultLocalTime;

    /// <summary>The stored week start, or the default when none has been chosen.</summary>
    public static DayOfWeek EffectiveWeekStart(DayOfWeek? chosen) => chosen ?? DefaultWeekStartsOn;
}
