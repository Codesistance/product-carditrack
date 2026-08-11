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
    /// A short, empathetic MedGemma-generated line describing a CardiMember's current status —
    /// fetched after the dashboard's own load so it never blocks first paint. May return a null
    /// <see cref="CurrentStatusMessageResponse.Message"/> when there's nothing to say yet.
    /// </summary>
    Task<CurrentStatusMessageResponse> GetCurrentStatusAsync(Guid cardiMemberId, CancellationToken ct = default);

    /// <summary>
    /// The member's most recent daily family digest (M1-13's summary card). Throws
    /// <see cref="ApiException"/> with a 404 when none has been generated yet — callers show an
    /// empty state rather than treating that as a failure.
    /// </summary>
    Task<DigestResponse> GetDigestAsync(Guid cardiMemberId, CancellationToken ct = default);

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

    /// <summary>Removes one alert from the caregiver's own lists (M1-10 card action).</summary>
    Task DeleteAlertAsync(Guid alertId, CancellationToken ct = default);
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

    // ---- Data-completeness notifications ----

    /// <summary>The caller's notification inbox, priority-ranked.</summary>
    Task<NotificationListResponse> GetNotificationsAsync(
        string? state = null,
        string? category = null,
        bool? owned = null,
        int? limit = null,
        CancellationToken ct = default);

    /// <summary>
    /// Badge count, safety banners and the dashboard card slots in one call — what the dashboard
    /// and the tab badge both read on appearing.
    /// </summary>
    Task<NotificationSummaryResponse> GetNotificationSummaryAsync(CancellationToken ct = default);

    /// <summary>Records that the caller has laid eyes on it. Only the first sighting counts.</summary>
    Task MarkNotificationSeenAsync(Guid notificationId, CancellationToken ct = default);

    /// <summary>Puts it off. The server clamps the duration to the rule's maximum.</summary>
    Task<NotificationResponse> SnoozeNotificationAsync(
        Guid notificationId, TimeSpan? duration = null, CancellationToken ct = default);

    /// <summary>
    /// Turns it off for good. <paramref name="acknowledgedConsequence"/> is required for
    /// safety-class rules and the server rejects the call without it.
    /// </summary>
    Task DismissNotificationAsync(
        Guid notificationId, bool acknowledgedConsequence = false, CancellationToken ct = default);

    /// <summary>Everything the caller has silenced.</summary>
    Task<List<NotificationMuteResponse>> GetNotificationMutesAsync(CancellationToken ct = default);

    Task RemoveNotificationMuteAsync(Guid muteId, CancellationToken ct = default);

    /// <summary>"Show me everything again" — clears every mute the caller holds.</summary>
    Task ResetNotificationMutesAsync(CancellationToken ct = default);

    /// <summary>Sets the caller's IANA time zone — what the timezone nudge sends the user to do.</summary>
    Task UpdateTimeZoneAsync(string timeZoneId, CancellationToken ct = default);

    // ---- Push delivery spine (notification_engine.md Phase 3) ----

    /// <summary>Upserts this device's push token — doubles as the reachability heartbeat (§4).</summary>
    Task<PushDeviceTokenResponse> RegisterPushDeviceAsync(
        RegisterPushDeviceRequest request, CancellationToken ct = default);

    Task UnregisterPushDeviceAsync(string deviceId, CancellationToken ct = default);

    /// <summary>
    /// Posted from the background push handler, before any user interaction. Anonymous — no
    /// bearer token attached, authorized by the payload's <c>ackToken</c> instead (§7.2 C3).
    /// </summary>
    Task AckDeliveredAsync(Guid deliveryId, string ackToken, CancellationToken ct = default);

    Task<NotificationPreferenceResponse> GetNotificationPreferencesAsync(CancellationToken ct = default);

    Task<NotificationPreferenceResponse> UpdateNotificationPreferencesAsync(
        UpdateNotificationPreferenceRequest request, CancellationToken ct = default);
}
