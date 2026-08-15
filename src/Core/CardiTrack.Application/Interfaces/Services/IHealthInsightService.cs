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

    /// <summary>
    /// A short, empathetic line describing a CardiMember's current status, for the Dashboard's
    /// hero card. Cached per member for a few minutes so a dashboard load never pays for a fresh
    /// model call — see the implementation for the TTL. Throws
    /// <see cref="KeyNotFoundException"/> when the user may not view that member's health data.
    /// </summary>
    Task<CurrentStatusMessageResponse> GetCurrentStatusMessageAsync(
        Guid requestingUserId, Guid cardiMemberId, CancellationToken ct = default);

    /// <summary>
    /// Answers a caregiver's free-text question about a CardiMember from the readings already
    /// on file. The model cannot look anything else up, set an alert, or follow instructions
    /// in the question. Throws <see cref="KeyNotFoundException"/> when the user may not view
    /// that member's health data. The exchange is not stored.
    /// </summary>
    Task<MemberAskResponse> AskAboutMemberAsync(
        Guid requestingUserId, Guid cardiMemberId, string question, CancellationToken ct = default);
}
