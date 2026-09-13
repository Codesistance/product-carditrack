using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Reports;

/// <summary>
/// Which journal books an export may carry. The Family running digest is a live
/// glance and the wearer copy is not a finished book a clinician would be handed.
/// </summary>
public static class ReportJournalScope
{
    public static readonly DigestAudience[] FinishedBooks =
    [
        DigestAudience.Daybook,
        DigestAudience.Weekbook,
        DigestAudience.Monthbook
    ];

    public static bool IsFinishedBook(DigestAudience audience) =>
        audience is DigestAudience.Daybook
            or DigestAudience.Weekbook
            or DigestAudience.Monthbook;

    public static bool DayIsInRange(DateOnly? day, DateOnly from, DateOnly to) =>
        day is null || (day.Value >= from && day.Value <= to);

    /// <summary>
    /// How far back a Daybook or Weekbook chart looks — the same fortnight the
    /// journal page draws. Kept here so the export gather cannot drift from the
    /// page by importing mobile chart code.
    /// </summary>
    public const int DayAndWeekChartDays = 14;

    /// <summary>
    /// How far back a Monthbook chart looks — the same 30 days the journal page
    /// draws. A calendar month is 28–31; a fixed 30 keeps every month the same width.
    /// </summary>
    public const int MonthChartDays = 30;

    /// <summary>
    /// The days whose readings a PDF chart should cover. A pinned journal entry
    /// charts the same window the journal page does — a fortnight ending on that
    /// day, or 30 days for a Monthbook — even though the request range is that
    /// one day. Health-data exports keep the range the caregiver picked.
    /// </summary>
    public static (DateOnly From, DateOnly To) ChartWindow(
        DateOnly from,
        DateOnly to,
        DateOnly? journalEntryDate,
        DigestAudience? audience)
    {
        if (journalEntryDate is not { } day)
            return (from, to);

        var span = audience == DigestAudience.Monthbook ? MonthChartDays : DayAndWeekChartDays;
        return (day.AddDays(-(span - 1)), day);
    }
}
