using System.Globalization;
using CardiTrack.Mobile.Core.Api;

namespace CardiTrack.Mobile.Core.Journal;

/// <summary>
/// How a CardiJournal entry's period is said to a caregiver.
/// </summary>
/// <remarks>
/// In Core rather than beside the rest of the presentation helpers because it is pure text over a
/// date, and the one part of that presentation worth pinning with tests — the rest of
/// <c>JournalPresentation</c> builds MAUI views, which cannot be constructed without a host.
/// </remarks>
public static class JournalLabels
{
    /// <summary>
    /// The period an entry covers, said the way its cadence asks for: a day names itself, a week
    /// names its span.
    /// </summary>
    /// <param name="localDate">
    /// The entry's own date. For a Weekbook this is the week's <em>last</em> day, so the span is
    /// read backwards from it.
    /// </param>
    /// <param name="today">
    /// The reader's today, for the relative day labels. Passed in rather than read from the clock
    /// so the relative wording is testable and so a caller can hold one "now" across a list.
    /// </param>
    public static string PeriodLabel(JournalCadence cadence, DateOnly localDate, DateOnly today) =>
        cadence switch
        {
            JournalCadence.Weekbook => WeekLabel(localDate),
            _ => DayLabel(localDate, today),
        };

    /// <summary>
    /// "Yesterday" for the day just gone, the weekday for the rest of the week, and the date
    /// beyond that — the way someone talks about their own week rather than a row of dates.
    /// </summary>
    /// <remarks>
    /// Against the reader's today, not the member's: this is the reader's label for when they are
    /// reading, and a caregiver in another timezone reading "Yesterday" about the day their own
    /// yesterday was is the sentence they would have said themselves.
    /// </remarks>
    public static string DayLabel(DateOnly date, DateOnly today)
    {
        var age = today.DayNumber - date.DayNumber;

        return age switch
        {
            0 => "Today",
            1 => "Yesterday",
            > 1 and < 7 => date.ToString("dddd", CultureInfo.CurrentCulture),
            _ => date.ToString("d MMMM", CultureInfo.CurrentCulture),
        };
    }

    /// <summary>
    /// The week's span, from the entry's end date back six days — "Mon 3 – Sun 9 August", or
    /// "Mon 28 July – Sun 3 August" when it straddles two months.
    /// </summary>
    /// <remarks>
    /// Both ends are named. A week labelled only by its last day would ask the caregiver to do the
    /// arithmetic to work out which week they are reading, and the span is the one thing about a
    /// Weekbook that a Daybook's label does not already teach them. No relative wording: "last
    /// week" is ambiguous the moment a caregiver is reading a fortnight-old entry, where
    /// "Yesterday" on a day never is.
    /// </remarks>
    public static string WeekLabel(DateOnly weekEnd)
    {
        var weekStart = weekEnd.AddDays(-6);
        var culture = CultureInfo.CurrentCulture;

        // The month is said once when both ends share it, and twice when they do not.
        var startFormat = weekStart.Month == weekEnd.Month ? "ddd d" : "ddd d MMMM";

        return $"{weekStart.ToString(startFormat, culture)} – {weekEnd.ToString("ddd d MMMM", culture)}";
    }
}
