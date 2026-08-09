using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;

namespace CardiTrack.Mobile.Core.Api;

public interface ICardiTrackApiClient
{
    Task<OnboardingStatusResponse> GetOnboardingStatusAsync(CancellationToken ct = default);

    /// <summary>
    /// Creates organization, trial subscription, and user in one atomic server call.
    /// Preferred over CreateOrganizationAsync + CreateUserAsync, which can orphan an
    /// organization if the app dies between the two requests.
    /// </summary>
    Task<OnboardingSetupResponse> SetupAsync(OnboardingSetupRequest request, CancellationToken ct = default);

    Task<OrganizationResponse> CreateOrganizationAsync(CreateOrganizationRequest request, CancellationToken ct = default);
    Task<UserResponse> CreateUserAsync(CreateUserRequest request, CancellationToken ct = default);
    Task<CardiMemberResponse> CreateCardiMemberAsync(CreateCardiMemberRequest request, CancellationToken ct = default);
    Task<List<CardiMemberResponse>> GetCardiMembersAsync(CancellationToken ct = default);

    /// <summary>Full profile for the CardiMember Detail screen (M1-13).</summary>
    Task<CardiMemberDetailResponse> GetCardiMemberAsync(Guid cardiMemberId, CancellationToken ct = default);

    /// <summary>Saves the edit form (M1-14).</summary>
    Task<CardiMemberDetailResponse> UpdateCardiMemberAsync(
        Guid cardiMemberId, UpdateCardiMemberRequest request, CancellationToken ct = default);

    /// <summary>Removes a CardiMember (M1-13 danger zone).</summary>
    Task RemoveCardiMemberAsync(Guid cardiMemberId, CancellationToken ct = default);

    Task<MonitoringPauseResponse> PauseMonitoringAsync(
        Guid cardiMemberId, PauseMonitoringRequest request, CancellationToken ct = default);

    Task<MonitoringPauseResponse> ResumeMonitoringAsync(Guid cardiMemberId, CancellationToken ct = default);

    Task<DashboardResponse> GetDashboardAsync(Guid cardiMemberId, CancellationToken ct = default);

    /// <summary>
    /// One page of alerts for the Alerts List (M1-10), newest first, across every CardiMember
    /// the signed-in user may read.
    /// </summary>
    /// <param name="severity">green/yellow/orange/red, or null for any.</param>
    /// <param name="status">new/acknowledged/resolved, or null for any.</param>
    Task<AlertListResponse> GetAlertsAsync(
        string? severity = null,
        string? status = null,
        DateTime? from = null,
        DateTime? to = null,
        int? limit = null,
        CancellationToken ct = default);

    /// <summary>Marks one alert as handled (M1-10 card action).</summary>
    Task<AlertAcknowledgementResponse> AcknowledgeAlertAsync(Guid alertId, CancellationToken ct = default);
    Task<DeviceListResponse> GetDevicesAsync(Guid cardiMemberId, CancellationToken ct = default);

    /// <summary>M1-15 device management.</summary>
    Task DisconnectDeviceAsync(Guid cardiMemberId, Guid deviceId, CancellationToken ct = default);

    Task<DeviceResponse> SetPrimaryDeviceAsync(Guid cardiMemberId, Guid deviceId, CancellationToken ct = default);

    Task<DeviceResponse> RefreshDeviceConnectionAsync(
        Guid cardiMemberId, Guid deviceId, CancellationToken ct = default);

    /// <summary>
    /// Pulls every connected device now rather than waiting for the scheduled sync — what the
    /// dashboard's refresh button does (issue #67).
    /// </summary>
    Task<DeviceSyncResultResponse> SyncDevicesAsync(Guid cardiMemberId, CancellationToken ct = default);
    Task<OAuthInitiationResponse> InitiateDeviceConnectionAsync(Guid cardiMemberId, ConnectDeviceRequest request, CancellationToken ct = default);
    Task<DeviceResponse> CompleteDeviceConnectionAsync(string provider, OAuthCallbackRequest request, CancellationToken ct = default);

    /// <summary>Asks the API to resend the Auth0 verification email. Anonymous; always succeeds server-side.</summary>
    Task ResendVerificationAsync(string email, CancellationToken ct = default);
}
