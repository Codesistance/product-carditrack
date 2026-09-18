using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Domain.Enums;
using CardiTrack.Mobile.Core.Api;

namespace CardiTrack.Mobile.Core.Export;

/// <summary>
/// The generate/consent snapshot a journal export sends. Kept off the pages so
/// the list (all Days / Weeks / Months in the window) and a single entry mint
/// the same fingerprint the generate call will consume.
/// </summary>
public static class JournalExportRequests
{
    /// <summary>Matches <c>GenerateReportValidator.MaxRangeDays</c>.</summary>
    public const int MaxRangeDays = 365;

    public static DigestAudience Audience(JournalCadence cadence) => cadence switch
    {
        JournalCadence.Weekbook => DigestAudience.Weekbook,
        JournalCadence.Monthbook => DigestAudience.Monthbook,
        _ => DigestAudience.Daybook
    };

    /// <summary>
    /// Inclusive window ending on <paramref name="today"/>. A null window is
    /// "All time", capped at a year so the request stays inside the API's ceiling.
    /// </summary>
    public static (DateOnly From, DateOnly To) Window(DateOnly today, int? windowDays)
    {
        var span = windowDays is { } days
            ? Math.Clamp(days, 1, MaxRangeDays)
            : MaxRangeDays;
        return (today.AddDays(-(span - 1)), today);
    }

    /// <summary>
    /// Widens a picked range to whole periods of the cadence: a Weekbook export cannot start on
    /// a Wednesday and a Monthbook export cannot end mid-month, because the entries themselves
    /// cover whole weeks and whole months — half a period asked for is a whole one returned.
    /// Days are already whole. Ends are ordered, and the result stays inside the API's ceiling.
    /// </summary>
    public static (DateOnly From, DateOnly To) SnapToCadence(
        JournalCadence cadence, DateOnly from, DateOnly to)
    {
        if (from > to)
            (from, to) = (to, from);

        (from, to) = cadence switch
        {
            JournalCadence.Weekbook => (StartOfWeek(from), StartOfWeek(to).AddDays(6)),
            JournalCadence.Monthbook => (
                new DateOnly(from.Year, from.Month, 1),
                new DateOnly(to.Year, to.Month, DateTime.DaysInMonth(to.Year, to.Month))),
            _ => (from, to),
        };

        // The ceiling trims the start, not the end: the recent period is the one being asked
        // about, so a range too long loses its oldest entries rather than its newest.
        if (to.DayNumber - from.DayNumber + 1 > MaxRangeDays)
            from = to.AddDays(-(MaxRangeDays - 1));

        return (from, to);
    }

    /// <summary>How many periods of the cadence a snapped range covers, for the caregiver to
    /// read back before they commit to the export.</summary>
    public static int PeriodCount(JournalCadence cadence, DateOnly from, DateOnly to)
    {
        var days = to.DayNumber - from.DayNumber + 1;
        return cadence switch
        {
            JournalCadence.Weekbook => Math.Max(1, (int)Math.Ceiling(days / 7d)),
            JournalCadence.Monthbook => Math.Max(
                1, ((to.Year - from.Year) * 12) + to.Month - from.Month + 1),
            _ => Math.Max(1, days),
        };
    }

    /// <summary>Monday of the week <paramref name="date"/> falls in.</summary>
    private static DateOnly StartOfWeek(DateOnly date) =>
        date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

    public static RecordExportConsentRequest Consent(
        Guid memberId,
        DateOnly from,
        DateOnly to,
        ReportFormat format,
        DigestAudience audience,
        DateOnly? entryDate,
        ExportConsentMethod method) =>
        Snapshot(memberId, from, to, format, audience, entryDate, method, consentToken: null)
            .Consent;

    public static GenerateReportRequest Generate(
        Guid memberId,
        string? title,
        DateOnly from,
        DateOnly to,
        ReportFormat format,
        DigestAudience audience,
        DateOnly? entryDate,
        string consentToken) =>
        Snapshot(memberId, from, to, format, audience, entryDate, method: null, consentToken, title)
            .Generate;

    private static (RecordExportConsentRequest Consent, GenerateReportRequest Generate) Snapshot(
        Guid memberId,
        DateOnly from,
        DateOnly to,
        ReportFormat format,
        DigestAudience audience,
        DateOnly? entryDate,
        ExportConsentMethod? method,
        string? consentToken,
        string? title = null)
    {
        // PDF draws the same fortnight (or month) the journal page charts. CSV has
        // no picture, so the flag stays off and the fingerprint stays journals-only.
        var includeTrends = format == ReportFormat.Pdf;

        var consent = new RecordExportConsentRequest
        {
            CardiMemberIds = [memberId],
            DateRangeFrom = from,
            DateRangeTo = to,
            Format = format,
            IncludeMetrics = false,
            IncludeTrends = includeTrends,
            IncludeAlerts = false,
            IncludeJournals = true,
            IncludeNotices = false,
            IncludeDevices = false,
            JournalEntryDate = entryDate,
            JournalAudience = audience,
            Method = method ?? ExportConsentMethod.Password,
            AcceptedResponsibility = true
        };

        var generate = new GenerateReportRequest
        {
            CardiMemberIds = [memberId],
            DateRangeFrom = from,
            DateRangeTo = to,
            Format = format,
            IncludeMetrics = false,
            IncludeTrends = includeTrends,
            IncludeAlerts = false,
            IncludeJournals = true,
            IncludeNotices = false,
            IncludeDevices = false,
            JournalEntryDate = entryDate,
            JournalAudience = audience,
            ConsentToken = consentToken,
            Title = title
        };

        return (consent, generate);
    }
}
