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

    /// <summary>
    /// Adds a CardiMember.
    /// </summary>
    /// <param name="idempotencyKey">
    /// This attempt’s own name, held by the form across retries. A create whose response is lost
    /// on the way back may still have succeeded; sending the same key again returns the member
    /// that attempt made instead of adding a second person to the care circle.
    /// </param>
    Task<CardiMemberResponse> CreateCardiMemberAsync(
        CreateCardiMemberRequest request, CancellationToken ct = default, string? idempotencyKey = null);
    Task<List<CardiMemberResponse>> GetCardiMembersAsync(CancellationToken ct = default);

    /// <summary>Full profile for the CardiMember Detail screen (M1-13).</summary>
    Task<CardiMemberDetailResponse> GetCardiMemberAsync(Guid cardiMemberId, CancellationToken ct = default);

    /// <summary>
    /// The same profile with its metric series ending on <paramref name="seriesEndsOn"/> rather
    /// than today. For a journal entry, whose charts draw the period it accounts for: a
    /// Monthbook's is a whole series ending a month or more ago, which the live profile's series,
    /// always running to today, cannot reach. Never cached: the payload is the member's current
    /// profile under a key the profile's own invalidation (an edit, a pause, a removal) does not
    /// spell, and a journal entry read offline draws from the live profile instead.
    /// </summary>
    Task<CardiMemberDetailResponse> GetCardiMemberAsync(
        Guid cardiMemberId, DateOnly seriesEndsOn, CancellationToken ct = default);

    /// <summary>Saves the edit form (M1-14).</summary>
    Task<CardiMemberDetailResponse> UpdateCardiMemberAsync(
        Guid cardiMemberId, UpdateCardiMemberRequest request, CancellationToken ct = default);

    /// <summary>
    /// Records that the health background was read and found still current, without changing it.
    /// </summary>
    /// <remarks>
    /// Its own call rather than re-saving the form, because the form cannot say this. An update is
    /// a full replacement, so it carries the notes whether or not anybody looked at them, and the
    /// server only re-dates them when the text actually changes. This is the caregiver saying the
    /// unchanged text still stands.
    /// </remarks>
    Task<CardiMemberDetailResponse> ConfirmMedicalNotesAsync(
        Guid cardiMemberId, CancellationToken ct = default);

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

    /// <summary>What an alarm may legally be built from — the builder's option list.</summary>
    Task<AlarmCatalogueResponse> GetAlarmCatalogueAsync(CancellationToken ct = default);

    /// <summary>
    /// The alarms that apply to one CardiMember: account-level defaults folded together with this
    /// member's overrides and additions, each saying where it came from and where it stands.
    /// </summary>
    Task<IReadOnlyList<MetricAlarmResponse>> GetMemberAlarmsAsync(
        Guid cardiMemberId, CancellationToken ct = default);

    /// <summary>Adds an alarm for this CardiMember alone. Primary caregiver only.</summary>
    Task<MetricAlarmResponse> CreateMemberAlarmAsync(
        Guid cardiMemberId, SaveMetricAlarmRequest request, CancellationToken ct = default);

    /// <summary>
    /// Sets what applies to this CardiMember for one alarm — editing their own row, or writing an
    /// override of an account default. Saving with <c>IsEnabled</c> false is how a member opts out
    /// of an inherited alarm.
    /// </summary>
    Task<MetricAlarmResponse> SaveMemberAlarmAsync(
        Guid cardiMemberId, Guid alarmId, SaveMetricAlarmRequest request, CancellationToken ct = default);

    /// <summary>
    /// Removes what this member has of their own for an alarm — reverting an override to the
    /// account default, or deleting an alarm that was only ever theirs.
    /// </summary>
    Task DeleteMemberAlarmAsync(Guid cardiMemberId, Guid alarmId, CancellationToken ct = default);

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
    /// The suggestion shown as "Something to try" on CardiMember Details — grounded in the
    /// member's own readings and public-health guidelines, never a diagnosis or a treatment
    /// change. Generated by the pipeline's batch pass; a blank
    /// <see cref="AdviseResponse.Suggestion"/> means there's nothing to say yet, the same shape
    /// <see cref="GetCurrentStatusAsync"/> uses.
    /// </summary>
    Task<AdviseResponse> GetAdviseAsync(Guid cardiMemberId, CancellationToken ct = default);

    /// <summary>
    /// The longer view of this member — where their readings have been going over the weeks. A
    /// blank narrative means there is not yet a month of readings to describe a trajectory from,
    /// which is the learning state rather than a failure.
    /// </summary>
    Task<TrendInsightResponse> GetTrendAsync(Guid cardiMemberId, CancellationToken ct = default);

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
    /// A standing fact the family volunteered. Stored as an already-answered permanent row —
    /// not an ask, so it does not occupy the pending slot.
    /// </summary>
    Task<QuestionnaireResponse> OfferStandingFactAsync(
        Guid cardiMemberId, OfferStandingFactRequest request, CancellationToken ct = default);

    /// <summary>
    /// One page of alerts for the Alerts List (M1-10), newest first, across every CardiMember
    /// the signed-in user may read — or one of them, with <paramref name="cardiMemberId"/>.
    /// </summary>
    /// <param name="severity">green/yellow/orange/red, or null for any.</param>
    /// <param name="status">new/acknowledged/resolved, or null for any.</param>
    /// <param name="cardiMemberId">
    /// Narrows the page to one CardiMember — what the dashboard card's Alerts button asks for, so
    /// a caregiver arriving from a member's card is not handed everyone's alerts to sift. Null
    /// for every member they may read.
    /// </param>
    Task<AlertListResponse> GetAlertsAsync(
        string? severity = null,
        string? status = null,
        DateTime? from = null,
        DateTime? to = null,
        int? limit = null,
        Guid? cardiMemberId = null,
        CancellationToken ct = default);

    /// <summary>
    /// The last page the device saved for exactly these arguments, without going near the
    /// network — or null when there is none, it has aged out, or the device cannot read it.
    /// For a screen to put on the wall while <see cref="GetAlertsAsync"/> fetches the live one:
    /// the alert list used to open onto a loading card on every landing, when the previous
    /// answer was sitting encrypted on the device the whole time.
    /// </summary>
    Task<AlertListResponse?> PeekAlertsAsync(
        string? severity = null,
        string? status = null,
        DateTime? from = null,
        DateTime? to = null,
        int? limit = null,
        Guid? cardiMemberId = null,
        CancellationToken ct = default);

    /// <summary>Marks one alert as handled (M1-10 card action).</summary>
    /// <param name="answer">
    /// What the caregiver did about it — a canned code from the alert's <c>responseOptions</c>,
    /// a note, or both. Null keeps the bodyless form, which the server still accepts.
    /// </param>
    Task<AlertAcknowledgementResponse> AcknowledgeAlertAsync(
        Guid alertId, AlertAnswerRequest? answer = null, CancellationToken ct = default);

    /// <summary>
    /// Closes an alert on the family's say-so: the condition is dealt with, and the rule may fire
    /// again. Final for caregivers — there is no undo-close, mirroring a system resolution.
    /// </summary>
    Task<AlertAcknowledgementResponse> CloseAlertAsync(
        Guid alertId, AlertAnswerRequest? answer = null, CancellationToken ct = default);

    /// <summary>
    /// Tells the API a caregiver has arrived, so the medical model can be loaded before they get
    /// as far as asking it something. Fire-and-forget: the server answers immediately whatever it
    /// decides to do, and a failure costs nothing but the head start.
    /// </summary>
    Task PrepareAssistantAsync(CancellationToken ct = default);

    /// <summary>
    /// Sends one member-chat message, auto-creating or continuing the caregiver's active session
    /// for this member. No Figma frame — as-built, see the design-sync backlog.
    /// </summary>
    Task<MemberChatMessageResponse> SendMemberChatMessageAsync(
        Guid cardiMemberId, MemberChatMessageRequest request, CancellationToken ct = default);

    /// <summary>
    /// <see cref="SendMemberChatMessageAsync"/> as a stream: each stage of the pipeline is
    /// reported to <paramref name="onStep"/> as it starts, so the pending bubble can say what is
    /// actually happening. A reply the server goes on to check arrives first as a draft through
    /// <paramref name="onDraft"/>, to show at once; the result is the reply as saved. When the
    /// server sent no <c>answer.updated</c>, the result is the very instance handed to
    /// <paramref name="onDraft"/>, so a caller can tell a replaced draft by reference, whatever
    /// part of it changed. Fails with the same
    /// <see cref="ApiException"/> statuses and messages as the plain send.
    /// </summary>
    Task<MemberChatMessageResponse> StreamMemberChatMessageAsync(
        Guid cardiMemberId, MemberChatMessageRequest request, IProgress<MemberChatStep>? onStep,
        IProgress<MemberChatMessageResponse>? onDraft = null, CancellationToken ct = default);

    /// <summary>The caregiver's active chat session and its turns for this member, or null if
    /// none exists — what a relaunched app resumes from.</summary>
    Task<MemberChatHistoryResponse?> GetCurrentMemberChatSessionAsync(
        Guid cardiMemberId, CancellationToken ct = default);

    /// <summary>The caregiver's completed conversations about this member, newest started
    /// first — what the chat sheet's history list shows. The active conversation is never in
    /// it, and an empty list is a caregiver who hasn't chatted yet, not an error.</summary>
    Task<MemberChatSessionListResponse> GetMemberChatSessionsAsync(
        Guid cardiMemberId, CancellationToken ct = default);

    /// <summary>One past conversation and its turns, opened from the history list.</summary>
    Task<MemberChatHistoryResponse> GetMemberChatSessionAsync(
        Guid cardiMemberId, Guid sessionId, CancellationToken ct = default);

    /// <summary>Ends the caregiver's active conversation about this member so the next message
    /// starts fresh. A null <c>EndedSessionId</c> means nothing was active — a fine outcome, not
    /// an error.</summary>
    Task<MemberChatEndSessionResponse> EndCurrentMemberChatSessionAsync(
        Guid cardiMemberId, CancellationToken ct = default);

    /// <summary>Reopens a completed conversation as the active one and returns its turns for
    /// the chat window to continue from — whatever was active is ended server-side in the same
    /// stroke.</summary>
    Task<MemberChatHistoryResponse> ContinueMemberChatSessionAsync(
        Guid cardiMemberId, Guid sessionId, CancellationToken ct = default);

    /// <summary>Permanently deletes conversations from the caregiver's history about this
    /// member. The caller has already warned that this cannot be undone; ids that no longer
    /// exist are skipped server-side, and <c>DeletedCount</c> says how many actually went.</summary>
    Task<MemberChatDeleteSessionsResponse> DeleteMemberChatSessionsAsync(
        Guid cardiMemberId, IReadOnlyList<Guid> sessionIds, CancellationToken ct = default);

    /// <summary>Question chips for the chat's empty state — deterministic server copy, no model
    /// call, instant. The caller treats a failure as "no chips" rather than an error.</summary>
    Task<MemberChatSuggestionsResponse> GetMemberChatSuggestionsAsync(
        Guid cardiMemberId, CancellationToken ct = default);

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

    /// <summary>
    /// Asks the API to re-read the last <paramref name="days"/> complete days of one device's
    /// history in the background — the M1-15 "Re-pull History" action. 202 with the queued
    /// request; progress then arrives on the device list as <c>historyRepull</c>.
    /// </summary>
    Task<DeviceHistoryRepullResponse> RequestHistoryRepullAsync(
        Guid cardiMemberId, Guid deviceId, int days, CancellationToken ct = default);

    Task<OAuthInitiationResponse> InitiateDeviceConnectionAsync(Guid cardiMemberId, ConnectDeviceRequest request, CancellationToken ct = default);
    Task<DeviceResponse> CompleteDeviceConnectionAsync(string provider, OAuthCallbackRequest request, CancellationToken ct = default);

    /// <summary>
    /// Mints an invitation for the wearer to authorize the device from their own phone, and returns
    /// it with the one-time URL to hand over.
    /// </summary>
    /// <remarks>
    /// <see cref="DeviceInviteResponse.Url"/> is populated only here. The status read below never
    /// returns it, so the caller must keep this one if it still needs it — the waiting screen holds
    /// it for the QR code it is displaying.
    /// </remarks>
    Task<DeviceInviteResponse> CreateDeviceInviteAsync(
        Guid cardiMemberId, CreateDeviceInviteRequest request, CancellationToken ct = default);

    /// <summary>One invitation's current state. What the waiting screen polls.</summary>
    Task<DeviceInviteResponse> GetDeviceInviteAsync(
        Guid cardiMemberId, Guid inviteId, CancellationToken ct = default);

    /// <summary>
    /// Cancels an invitation, returning its resulting state — which is <c>completed</c> rather than
    /// <c>revoked</c> when the wearer got there first.
    /// </summary>
    Task<DeviceInviteResponse> RevokeDeviceInviteAsync(
        Guid cardiMemberId, Guid inviteId, CancellationToken ct = default);

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

    /// <summary>
    /// Whether the caller has dismissed the Google-mandated health-data disclosure. Never served
    /// from the offline cache: a compliance banner decided by a stale answer is the wrong kind of
    /// last-known-good.
    /// </summary>
    Task<HealthDataDisclosureResponse> GetHealthDataDisclosureAsync(CancellationToken ct = default);

    /// <summary>
    /// Where this account stands with respect to deletion — read on sign-in as well as from
    /// Settings, so a caregiver who asked and changed their mind is told the request still stands.
    /// </summary>
    Task<AccountDeletionStatusResponse> GetAccountDeletionAsync(CancellationToken ct = default);

    /// <summary>
    /// Asks for this account and its members' health data to be deleted.
    /// </summary>
    /// <remarks>
    /// The server records the request and refuses the account from that moment; it does not erase
    /// anything for thirty days. A caller that gets a result here must sign the caregiver out —
    /// every other endpoint will refuse them, and staying on a signed-in session that cannot load
    /// anything looks like the app breaking rather than like a request being honoured.
    /// </remarks>
    Task<AccountDeletionStatusResponse> RequestAccountDeletionAsync(CancellationToken ct = default);

    /// <summary>Calls off an outstanding deletion request and restores the account.</summary>
    Task<AccountDeletionStatusResponse> CancelAccountDeletionAsync(CancellationToken ct = default);

    /// <summary>Records that the caller has read the disclosure; the banner hides only once this succeeds.</summary>
    Task DismissHealthDataDisclosureAsync(CancellationToken ct = default);

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

    // ---- Health data export (M1-17, Story 6.3) ----

    /// <summary>
    /// Queues an export. Returns immediately with a report id to poll.
    /// </summary>
    Task<ExportConsentResponse> RecordExportConsentAsync(
        RecordExportConsentRequest request, CancellationToken ct = default);

    /// <summary>
    /// Mints a token from the named in-force standing grant. 404 when that grant
    /// cannot be reused — the caller then runs the full confirmation.
    /// </summary>
    Task<ExportConsentResponse> ReuseExportConsentAsync(
        Guid consentId, GenerateReportRequest request, CancellationToken ct = default);

    Task<List<ExportConsentHistoryItem>> GetExportConsentsAsync(
        CancellationToken ct = default);

    Task RevokeExportConsentAsync(Guid consentId, CancellationToken ct = default);

    Task<ReportQueuedResponse> GenerateReportAsync(
        GenerateReportRequest request, CancellationToken ct = default);

    /// <summary>
    /// Polls a queued export. Null when the report is unknown, expired, or another user's — the
    /// three are indistinguishable by design.
    /// </summary>
    Task<ReportStatusResponse?> GetReportStatusAsync(string reportId, CancellationToken ct = default);

    /// <summary>
    /// Downloads a ready export's bytes, with the content type and filename the server chose.
    /// Streamed through the API rather than fetched from a bucket URL, so the download is
    /// authorized and audited like every other read of health data.
    /// </summary>
    Task<ReportFile> DownloadReportAsync(string reportId, CancellationToken ct = default);

    // ---- Cache-only peeks ----
    //
    // Each is the device's last saved answer to exactly the question its Get twin asks — the
    // same arguments produce the same key — or null when there is none, it has aged out, or the
    // device cannot read it. Never touches the network. For a screen to put on the wall while the
    // live call runs behind it (Offline.SnapshotRefresh); OriginOf on the returned task says when
    // the snapshot was saved. PeekAlertsAsync above is the original of the pattern.

    Task<List<CardiMemberResponse>?> PeekCardiMembersAsync(CancellationToken ct = default);
    Task<CardiMemberDetailResponse?> PeekCardiMemberAsync(Guid cardiMemberId, CancellationToken ct = default);
    Task<DashboardResponse?> PeekDashboardAsync(Guid cardiMemberId, CancellationToken ct = default);
    Task<AlertDetailResponse?> PeekAlertAsync(Guid alertId, CancellationToken ct = default);
    Task<DigestResponse?> PeekDigestAsync(Guid cardiMemberId, CancellationToken ct = default);
    Task<AdviseResponse?> PeekAdviseAsync(Guid cardiMemberId, CancellationToken ct = default);

    /// <summary>The cached longer view, without a network call. Null when nothing is cached.</summary>
    Task<TrendInsightResponse?> PeekTrendAsync(Guid cardiMemberId, CancellationToken ct = default);

    Task<IReadOnlyList<DigestResponse>?> PeekJournalEntriesAsync(
        Guid cardiMemberId,
        JournalCadence cadence,
        int limit,
        string? search = null,
        DateOnly? from = null,
        string? urgency = null,
        CancellationToken ct = default);

    Task<DigestResponse?> PeekJournalEntryAsync(
        Guid cardiMemberId,
        JournalCadence cadence,
        DateOnly localDate,
        CancellationToken ct = default);

    Task<QuestionnairesPageResponse?> PeekQuestionnairesAsync(
        Guid cardiMemberId,
        string? search = null,
        int page = 1,
        int pageSize = 20,
        CancellationToken ct = default);

    Task<DeviceListResponse?> PeekDevicesAsync(Guid cardiMemberId, CancellationToken ct = default);

    Task<NotificationListResponse?> PeekNotificationsAsync(
        string? state = null,
        string? category = null,
        bool? owned = null,
        int? limit = null,
        CancellationToken ct = default);

    Task<NotificationSummaryResponse?> PeekNotificationSummaryAsync(CancellationToken ct = default);
    Task<AlertPreferencesResponse?> PeekAlertPreferencesAsync(Guid cardiMemberId, CancellationToken ct = default);
    Task<IReadOnlyList<MetricAlarmResponse>?> PeekMemberAlarmsAsync(Guid cardiMemberId, CancellationToken ct = default);
    Task<AlarmCatalogueResponse?> PeekAlarmCatalogueAsync(CancellationToken ct = default);
    Task<JournalSettingsResponse?> PeekJournalSettingsAsync(Guid cardiMemberId, CancellationToken ct = default);
    Task<NotificationPreferenceResponse?> PeekNotificationPreferencesAsync(CancellationToken ct = default);
    Task<List<NotificationMuteResponse>?> PeekNotificationMutesAsync(CancellationToken ct = default);
    Task<CurrentStatusMessageResponse?> PeekCurrentStatusAsync(Guid cardiMemberId, CancellationToken ct = default);
    Task<MemberChatHistoryResponse?> PeekCurrentMemberChatSessionAsync(Guid cardiMemberId, CancellationToken ct = default);
    Task<MemberChatSessionListResponse?> PeekMemberChatSessionsAsync(Guid cardiMemberId, CancellationToken ct = default);
    Task<MemberChatSuggestionsResponse?> PeekMemberChatSuggestionsAsync(Guid cardiMemberId, CancellationToken ct = default);
    Task<List<ExportConsentHistoryItem>?> PeekExportConsentsAsync(CancellationToken ct = default);

    // ---- Families: membership, joining, and caregiver invitations ----

    /// <summary>Every family the caller belongs to, with their role and the members they may see in each.</summary>
    Task<IReadOnlyList<FamilySummary>> GetMyFamiliesAsync(CancellationToken ct = default);

    Task<IReadOnlyList<FamilySummary>?> PeekMyFamiliesAsync(CancellationToken ct = default);

    /// <summary>The people in one family. Any member of it may read this.</summary>
    Task<IReadOnlyList<FamilyMemberSummary>> GetFamilyMembersAsync(Guid organizationId, CancellationToken ct = default);

    Task<IReadOnlyList<FamilyMemberSummary>?> PeekFamilyMembersAsync(Guid organizationId, CancellationToken ct = default);

    /// <summary>
    /// Hands the family and its plan to another member; the caller becomes a member in the same
    /// save. Returns the roster as it now stands.
    /// </summary>
    Task<IReadOnlyList<FamilyMemberSummary>> TransferFamilyAdminAsync(
        Guid organizationId, Guid userId, CancellationToken ct = default);

    /// <summary>Admin removes somebody from the family, and with it their view of its members.</summary>
    Task RemoveFamilyMemberAsync(Guid organizationId, Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Leaves a family. Refused (422) for its admin, who must hand it over first — or, alone in
    /// it, is told to delete their account instead.
    /// </summary>
    /// <remarks>
    /// On success the device's whole read cache is dropped, not only the family's own keys: every
    /// dashboard, alert list, journal and member profile the caregiver has opened is a saved copy
    /// of health data they may no longer read, and each is filed under its own path. Evicting the
    /// handful this call knows about would leave the rest to be served offline for the cache's
    /// lifetime, which is the one thing leaving a family has to stop.
    /// </remarks>
    Task LeaveFamilyAsync(Guid organizationId, CancellationToken ct = default);

    /// <summary>
    /// Asks to join the family with this Family ID. The receipt is identical for a code that does
    /// not exist, so nothing about it says whether the ask landed anywhere.
    /// </summary>
    Task<FamilyJoinRequestReceipt> RequestToJoinFamilyAsync(string familyId, CancellationToken ct = default);

    /// <summary>The asks the caller has made — pending, declined or expired.</summary>
    Task<IReadOnlyList<FamilyJoinRequestSummary>> GetMyJoinRequestsAsync(CancellationToken ct = default);

    Task<IReadOnlyList<FamilyJoinRequestSummary>?> PeekMyJoinRequestsAsync(CancellationToken ct = default);

    Task WithdrawJoinRequestAsync(Guid requestId, CancellationToken ct = default);

    /// <summary>Admin: who is waiting to be let into this family.</summary>
    Task<IReadOnlyList<PendingJoinRequest>> GetPendingJoinRequestsAsync(Guid organizationId, CancellationToken ct = default);

    Task<IReadOnlyList<PendingJoinRequest>?> PeekPendingJoinRequestsAsync(Guid organizationId, CancellationToken ct = default);

    /// <summary>Admin lets somebody in, choosing which members they get and their role.</summary>
    Task ApproveJoinRequestAsync(
        Guid organizationId, Guid requestId, ApproveJoinRequest decision, CancellationToken ct = default);

    Task DeclineJoinRequestAsync(Guid organizationId, Guid requestId, CancellationToken ct = default);

    /// <summary>
    /// Mints an invitation to watch one member and returns it with its link. The link comes back
    /// exactly once, here — the list never carries it — so the caller must keep it.
    /// </summary>
    Task<CaregiverInviteResponse> CreateCaregiverInviteAsync(
        Guid cardiMemberId, CreateCaregiverInviteRequest request, CancellationToken ct = default);

    /// <summary>Every invitation issued for this member, newest first, without their links.</summary>
    Task<IReadOnlyList<CaregiverInviteResponse>> GetCaregiverInvitesAsync(Guid cardiMemberId, CancellationToken ct = default);

    Task<IReadOnlyList<CaregiverInviteResponse>?> PeekCaregiverInvitesAsync(Guid cardiMemberId, CancellationToken ct = default);

    Task<CaregiverInviteResponse> RevokeCaregiverInviteAsync(
        Guid cardiMemberId, Guid inviteId, CancellationToken ct = default);

    /// <summary>
    /// What an invitation is for: two first names and a deadline. Needs a signed-in caller — the
    /// endpoint is deliberately not anonymous, so a guessed token never yields a name.
    /// </summary>
    Task<CaregiverInviteView> ViewCaregiverInviteAsync(string token, CancellationToken ct = default);

    /// <summary>Redeems an invitation for the signed-in user. Always admits as a member.</summary>
    Task<CaregiverInviteRedemption> AcceptCaregiverInviteAsync(string token, CancellationToken ct = default);

    Task DeclineCaregiverInviteAsync(string token, CancellationToken ct = default);
}

/// <summary>A downloaded export: the bytes, and what to call them when saving or sharing.</summary>
public sealed record ReportFile(byte[] Content, string ContentType, string FileName);
