using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Mobile.Core.Export;

/// <summary>
/// The generate/consent snapshot a chat transcript export sends. Kept off the page, like
/// <see cref="JournalExportRequests"/>, so the confirmation and the generate call that follows it
/// mint the same fingerprint — the export is refused outright if they disagree.
/// </summary>
public static class ChatTranscriptExportRequests
{
    /// <summary>
    /// The days the export is framed by: the conversation's own, clamped to the API's ceiling and
    /// ordered, so a clock skew or a thread reopened months later still produces a legal range.
    /// </summary>
    /// <remarks>
    /// Dates, not instants: the range is what the header, the filename and the fingerprint are
    /// built from, and a conversation that ran over midnight is two days on any of them. The
    /// caller passes local dates, because that is what the caregiver saw in the history list.
    /// </remarks>
    public static (DateOnly From, DateOnly To) Window(DateOnly started, DateOnly lastTurn)
    {
        var (from, to) = started <= lastTurn ? (started, lastTurn) : (lastTurn, started);

        // The ceiling trims the start: a conversation is read from its most recent turn back.
        if (to.DayNumber - from.DayNumber + 1 > JournalExportRequests.MaxRangeDays)
            from = to.AddDays(-(JournalExportRequests.MaxRangeDays - 1));

        return (from, to);
    }

    public static RecordExportConsentRequest Consent(
        Guid memberId,
        Guid sessionId,
        DateOnly from,
        DateOnly to,
        ReportFormat format,
        ExportConsentMethod method) =>
        Snapshot(memberId, sessionId, from, to, format, method, consentToken: null).Consent;

    public static GenerateReportRequest Generate(
        Guid memberId,
        Guid sessionId,
        string? title,
        DateOnly from,
        DateOnly to,
        ReportFormat format,
        string consentToken) =>
        Snapshot(memberId, sessionId, from, to, format, method: null, consentToken, title).Generate;

    private static (RecordExportConsentRequest Consent, GenerateReportRequest Generate) Snapshot(
        Guid memberId,
        Guid sessionId,
        DateOnly from,
        DateOnly to,
        ReportFormat format,
        ExportConsentMethod? method,
        string? consentToken,
        string? title = null)
    {
        // Every section off. They say nothing about a transcript — its content is the conversation
        // and its charts are the ones stored on the replies — and the API ignores them here, but
        // they are in the fingerprint, so leaving the defaults on would put "a month of readings,
        // alerts and trends" into what the caregiver confirmed.
        var consent = new RecordExportConsentRequest
        {
            CardiMemberIds = [memberId],
            DateRangeFrom = from,
            DateRangeTo = to,
            Format = format,
            IncludeMetrics = false,
            IncludeTrends = false,
            IncludeAlerts = false,
            IncludeJournals = false,
            IncludeNotices = false,
            IncludeDevices = false,
            ChatSessionId = sessionId,
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
            IncludeJournals = false,
            IncludeNotices = false,
            IncludeDevices = false,
            ChatSessionId = sessionId,
            ConsentToken = consentToken,
            Title = title
        };

        return (consent, generate);
    }
}
