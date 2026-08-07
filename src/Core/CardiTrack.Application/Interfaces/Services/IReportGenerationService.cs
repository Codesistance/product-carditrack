using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;

namespace CardiTrack.Application.Interfaces.Services;

public interface IReportGenerationService
{
    /// <summary>
    /// Enqueues async report generation. Returns immediately with a report ID to poll.
    /// Throws <see cref="KeyNotFoundException"/> unless the requesting user may view every
    /// CardiMember named in the request.
    /// </summary>
    Task<ReportQueuedResponse> GenerateAsync(Guid requestingUserId, GenerateReportRequest request);

    /// <summary>
    /// Returns current status (pending / ready / failed / expired), or null when the report is
    /// unknown, expired, or belongs to another user — the three are indistinguishable by design.
    /// </summary>
    Task<ReportStatusResponse?> GetStatusAsync(Guid requestingUserId, string reportId);

    /// <summary>
    /// Returns the raw file bytes and content-type for a ready report the requesting user owns.
    /// Throws <see cref="KeyNotFoundException"/> when it is unknown, expired, or another user's.
    /// </summary>
    Task<(byte[] Content, string ContentType, string FileName)> DownloadAsync(Guid requestingUserId, string reportId);
}
