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
}
