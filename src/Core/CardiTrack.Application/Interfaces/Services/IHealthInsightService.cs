using CardiTrack.Application.DTOs.Responses;

namespace CardiTrack.Application.Interfaces.Services;

public interface IHealthInsightService
{
    /// <summary>
    /// Analyses an alert for a CardiMember the requesting user is linked to.
    /// Throws <see cref="KeyNotFoundException"/> when the alert does not exist or the user
    /// may not view its CardiMember's health data — the two are indistinguishable by design.
    /// </summary>
    Task<AlertInsightResponse> AnalyzeAlertAsync(
        Guid requestingUserId, Guid alertId, CancellationToken ct = default);

    /// <summary>
    /// Analyses baseline trends for a CardiMember the requesting user is linked to.
    /// Throws <see cref="KeyNotFoundException"/> when the user may not view that member's health data.
    /// </summary>
    Task<BaselineInsightResponse> AnalyzeBaselineAsync(
        Guid requestingUserId, Guid cardiMemberId, CancellationToken ct = default);
}
