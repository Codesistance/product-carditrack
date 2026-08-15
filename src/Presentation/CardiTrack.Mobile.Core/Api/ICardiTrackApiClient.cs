using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;

namespace CardiTrack.Mobile.Core.Api;

public interface ICardiTrackApiClient
{
    /// <summary>
    /// Where a GET's payload came from — pass the very task the call returned. Null for a task
    /// this client did not produce, or one whose GET is no longer held anywhere.
    /// </summary>
    /// <remarks>
    /// Per call rather than per client on purpose; <see cref="CacheOrigin"/> says why. A screen
    /// showing the offline banner keeps the task of the load the banner speaks for and asks about
    /// that, instead of reading the origin of whatever GET happened to finish last.
    /// </remarks>
    CacheOrigin? OriginOf(Task call);

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

    /// <summary>Per-CardiMember alert-rule clusters with effective on/off state (M1-13).</summary>
    Task<AlertPreferencesResponse> GetAlertPreferencesAsync(Guid cardiMemberId, CancellationToken ct = default);

    /// <summary>Instant toggle for one alert rule. Off skips producer evaluation entirely.</summary>
    Task<AlertRuleSettingResponse> SetAlertRuleEnabledAsync(
        Guid cardiMemberId, string ruleId, bool enabled, CancellationToken ct = default);

    /// <summary>
    /// When this member's CardiJournal books are written, in their own local time, with the
    /// window and step a picker must stay inside.
    /// </summary>
    Task<JournalSettingsResponse> GetJournalSettingsAsync(
        Guid cardiMemberId, CancellationToken ct = default);

    /// <summary>
    /// Moves when this member's books are written. A null field restores that book's default.
    /// Primary caregiver only — the API answers 404 to anyone else.
    /// </summary>
    Task<JournalSettingsResponse> UpdateJournalSettingsAsync(
        Guid cardiMemberId, UpdateJournalSettingsRequest request, CancellationToken ct = default);

    Task<DashboardResponse> GetDashboardAsync(Guid cardiMemberId, CancellationToken ct = default);

    /// <summary>
    /// A short, empathetic MedGemma-generated read on a CardiMember's current state — a punchy
    /// <see cref="CurrentStatusMessageResponse.Headline"/> and the sentence under it — fetched
    /// after the dashboard's own load so it never blocks first paint. May return a null
    /// <see cref="CurrentStatusMessageResponse.Message"/> when there's nothing to say yet.
    /// </summary>
    Task<CurrentStatusMessageResponse> GetCurrentStatusAsync(Guid cardiMemberId, CancellationToken ct = default);

    /// <summary>
    /// The member's current family summary (M1-13's summary card), recomputed as their data
    /// moves. Throws <see cref="ApiException"/> with a 404 when none has been generated yet —
    /// callers show an empty state rather than treating that as a failure.
    /// </summary>
    Task<DigestResponse> GetDigestAsync(Guid cardiMemberId, CancellationToken ct = default);

    /// <summary>
    /// The member's daybook entries, newest first — one per finished day, which is what the Summaries
    /// tab lists. An empty list rather than a 404 when none has been written yet: "this member has
    /// no reviews yet" is an ordinary answer to a history question, and the first two days of a new
    /// member legitimately have none.
    /// </summary>
    /// <param name="cardiMemberId">The member whose reviews are being read.</param>
    /// <param name="limit">How many to ask for. The service clamps this into range.</param>
    /// <param name="search">
    /// Optional text filter over the review, its headline and its suggestion — applied
    /// server-side, before the limit, so it searches the history rather than the loaded page.
    /// </param>
    /// <param name="from">Optional earliest local day, inclusive.</param>
    /// <param name="urgency">
    /// Optional urgency tier in the wire vocabulary (watch / check-in / concerning / act-now).
    /// </param>
    /// <param name="ct">Cancels the read.</param>
    /// <param name="cadence">Which book to read — the Daybook series or the Weekbook series.</param>
    Task<IReadOnlyList<DigestResponse>> GetJournalEntriesAsync(
        Guid cardiMemberId,
        JournalCadence cadence,
        int limit,
        string? search = null,
        DateOnly? from = null,
        string? urgency = null,
        CancellationToken ct = default);

    /// <summary>
    /// One entry — the latest (and in practice only) book of <paramref name="cadence"/> dated
    /// <paramref name="localDate"/>. For a Weekbook that date is the week's <em>last day</em>.
    /// Throws <see cref="ApiException"/> with a 404 when none was written; the detail screen shows
    /// its error state rather than treating that as a fault.
    /// </summary>
    Task<DigestResponse> GetJournalEntryAsync(
        Guid cardiMemberId,
        JournalCadence cadence,
        DateOnly localDate,
        CancellationToken ct = default);

    /// <summary>
    /// Asks MedGemma a free-text question about this CardiMember, answered from the readings
    /// already on file. Not cached: every question is a live model call. Throws
    /// <see cref="ApiException"/> with 404 when the caller may not view the member.
    /// </summary>
    Task<MemberAskResponse> AskAboutMemberAsync(
        Guid cardiMemberId, AskMemberQuestionRequest request, CancellationToken ct = default);

    // ---- Questions the service asks the family ----

    /// <summary>
    /// The pending question (at most one, by design — the pipeline will not ask a second thing
    /// while the first is unanswered) plus a page of the answered history, newest first, optionally
    /// filtered to those whose question or answer text contains <paramref name="search"/>.
    /// </summary>
    Task<QuestionnairesPageResponse> GetQuestionnairesAsync(
        Guid cardiMemberId,
        string? search = null,
        int page = 1,
        int pageSize = 20,
        CancellationToken ct = default);

    /// <summary>Answers a pending question, or replaces an answer already given.</summary>
    Task<QuestionnaireResponse> AnswerQuestionnaireAsync(
        Guid questionnaireId, AnswerQuestionnaireRequest request, CancellationToken ct = default);

    /// <summary>Skips the question. It is never asked again; the record of asking survives.</summary>
    Task<QuestionnaireResponse> DismissQuestionnaireAsync(
        Guid questionnaireId, CancellationToken ct = default);

    /// <summary>
    /// Retires a question that has outlived the day it asked about, so it stops waiting on this
    /// family and stops blocking the next one. Called by the app when
    /// <see cref="Questionnaires.QuestionValidity"/> finds a card past its validity; the server
    /// checks the same thing against its own clock before acting, so a question still inside its
    /// window comes back unchanged rather than as an error.
    /// </summary>
    Task<QuestionnaireResponse> ExpireQuestionnaireAsync(
        Guid questionnaireId, CancellationToken ct = default);

    /// <summary>Removes the question and its answer outright.</summary>
    Task DeleteQuestionnaireAsync(Guid questionnaireId, CancellationToken ct = default);

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

    /// <summary>
    /// Puts an acknowledged alert back to unhandled — the undo behind M1-11's Undo button.
    /// Rejected for an alert the system has already resolved.
    /// </summary>
    Task<AlertAcknowledgementResponse> UnacknowledgeAlertAsync(Guid alertId, CancellationToken ct = default);

    /// <summary>One alert for the detail screen (M1-11 / M1-12 / M1-16).</summary>
    Task<AlertDetailResponse> GetAlertAsync(Guid alertId, CancellationToken ct = default);

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
