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
        var consent = new RecordExportConsentRequest
        {
            CardiMemberIds = [memberId],
            DateRangeFrom = from,
            DateRangeTo = to,
            Format = format,
            IncludeMetrics = false,
            IncludeTrends = false,
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
            IncludeTrends = false,
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
