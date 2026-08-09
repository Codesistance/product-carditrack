using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Interfaces.Services;

public interface IAlertService
{
    /// <summary>
    /// One page of alerts for the mobile Alerts List (M1-10), newest first.
    /// </summary>
    /// <param name="cardiMemberId">
    /// Narrows to a single member; null spans every member the user may read. Throws
    /// <see cref="KeyNotFoundException"/> when supplied but not readable by this user.
    /// </param>
    Task<AlertListResponse> GetAlertsAsync(
        Guid requestingUserId,
        Guid? cardiMemberId = null,
        AlertSeverity? severity = null,
        AlertStatus? status = null,
        DateTime? from = null,
        DateTime? to = null,
        int limit = 50,
        int offset = 0,
        CancellationToken ct = default);

    /// <summary>
    /// Marks one alert as handled by the requesting user. Idempotent: acknowledging an
    /// already-acknowledged alert keeps the original timestamp and acknowledger rather than
    /// rewriting who dealt with it. Throws <see cref="KeyNotFoundException"/> when the alert
    /// doesn't exist or belongs to a member the user may not read.
    /// </summary>
    Task<AlertAcknowledgementResponse> AcknowledgeAsync(
        Guid requestingUserId, Guid alertId, CancellationToken ct = default);
}
