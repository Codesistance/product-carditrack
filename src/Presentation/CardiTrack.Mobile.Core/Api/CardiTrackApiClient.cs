using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Core.Offline;
using CardiTrack.Shared.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CardiTrack.Mobile.Core.Api;

/// <summary>
/// Typed client over the CardiTrack API's ApiResponse envelope. All request bodies are
/// buffered JSON so the auth handler's 401 retry can re-send them.
/// </summary>
public sealed class CardiTrackApiClient : ICardiTrackApiClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly IOfflineReadCache? _cache;
    private readonly ILogger<CardiTrackApiClient> _logger;

    /// <summary>
    /// Where each GET's payload came from, keyed by the task that GET returned. Weak on the key,
    /// so an entry lives exactly as long as the caller still holds the call it describes —
    /// nothing to expire, nothing to bound, and no fact about one screen's call left lying around
    /// for another screen to read as its own.
    /// </summary>
    private readonly ConditionalWeakTable<Task, CacheOrigin> _origins = new();

    public CardiTrackApiClient(
        HttpClient http,
        IOfflineReadCache? cache = null,
        ILogger<CardiTrackApiClient>? logger = null)
    {
        _http = http;
        _cache = cache;
        _logger = logger ?? NullLogger<CardiTrackApiClient>.Instance;
    }

    public Task<OnboardingStatusResponse> GetOnboardingStatusAsync(CancellationToken ct = default) =>
        GetAsync<OnboardingStatusResponse>("api/Onboarding/status", ct);

    public Task<OnboardingSetupResponse> SetupAsync(OnboardingSetupRequest request, CancellationToken ct = default) =>
        PostAsync<OnboardingSetupRequest, OnboardingSetupResponse>("api/Onboarding/setup", request, ct);

    public Task<OrganizationResponse> CreateOrganizationAsync(CreateOrganizationRequest request, CancellationToken ct = default) =>
        PostAsync<CreateOrganizationRequest, OrganizationResponse>("api/Onboarding/organization", request, ct);

    public Task<UserResponse> CreateUserAsync(CreateUserRequest request, CancellationToken ct = default) =>
        PostAsync<CreateUserRequest, UserResponse>("api/Onboarding/user", request, ct);

    public Task<CardiMemberResponse> CreateCardiMemberAsync(CreateCardiMemberRequest request, CancellationToken ct = default) =>
        PostAsync<CreateCardiMemberRequest, CardiMemberResponse>("api/Onboarding/cardimember", request, ct);

    public Task<List<CardiMemberResponse>> GetCardiMembersAsync(CancellationToken ct = default) =>
        GetAsync<List<CardiMemberResponse>>(ApiPaths.CardiMembers, ct);

    public Task<List<CardiMemberResponse>?> PeekCardiMembersAsync(CancellationToken ct = default) =>
        PeekAsync<List<CardiMemberResponse>>(ApiPaths.CardiMembers, ct);

    public Task<CardiMemberDetailResponse> GetCardiMemberAsync(Guid cardiMemberId, CancellationToken ct = default) =>
        GetAsync<CardiMemberDetailResponse>(ApiPaths.CardiMember(cardiMemberId), ct);

    public Task<CardiMemberDetailResponse?> PeekCardiMemberAsync(Guid cardiMemberId, CancellationToken ct = default) =>
        PeekAsync<CardiMemberDetailResponse>(ApiPaths.CardiMember(cardiMemberId), ct);

    public async Task<CardiMemberDetailResponse> UpdateCardiMemberAsync(
        Guid cardiMemberId, UpdateCardiMemberRequest request, CancellationToken ct = default)
    {
        var updated = await SendAsync<UpdateCardiMemberRequest, CardiMemberDetailResponse>(
            HttpMethod.Put, ApiPaths.CardiMember(cardiMemberId), request, ct);
        await EvictAsync(MemberProfileKeys(cardiMemberId));
        return updated;
    }

    public async Task RemoveCardiMemberAsync(Guid cardiMemberId, CancellationToken ct = default)
    {
        await SendNoDataAsync(HttpMethod.Delete, ApiPaths.CardiMember(cardiMemberId), ct);
        // Every key for this member the client can spell. Parameterised list pages (a filtered
        // alert list, a journal search) cannot be enumerated without an index and age out
        // within IOfflineReadCache.Lifetime or at sign-out; the plain ones go now.
        await EvictAsync(
            ApiPaths.CardiMember(cardiMemberId),
            ApiPaths.Dashboard(cardiMemberId),
            ApiPaths.Devices(cardiMemberId),
            ApiPaths.AlertPreferences(cardiMemberId),
            ApiPaths.MemberAlarms(cardiMemberId),
            ApiPaths.JournalSettings(cardiMemberId),
            ApiPaths.CurrentStatus(cardiMemberId),
            ApiPaths.Digest(cardiMemberId),
            ApiPaths.Advise(cardiMemberId),
            ApiPaths.Questionnaires(cardiMemberId, null, DefaultQuestionnairePage, DefaultQuestionnairePageSize),
            ApiPaths.Alerts(null, null, null, null, null, cardiMemberId),
            ApiPaths.CardiMembers,
            ApiPaths.NotificationSummary);
    }

    public async Task<MonitoringPauseResponse> PauseMonitoringAsync(
        Guid cardiMemberId, PauseMonitoringRequest request, CancellationToken ct = default)
    {
        var paused = await PostAsync<PauseMonitoringRequest, MonitoringPauseResponse>(
            $"api/v1/cardimembers/{cardiMemberId}/pause", request, ct);
        await EvictAsync(MemberProfileKeys(cardiMemberId));
        return paused;
    }

    public async Task<MonitoringPauseResponse> ResumeMonitoringAsync(
        Guid cardiMemberId, CancellationToken ct = default)
    {
        var resumed = await SendAsync<MonitoringPauseResponse>(
            HttpMethod.Delete, $"api/v1/cardimembers/{cardiMemberId}/pause", ct);
        await EvictAsync(MemberProfileKeys(cardiMemberId));
        return resumed;
    }

    public Task<AlertPreferencesResponse> GetAlertPreferencesAsync(
        Guid cardiMemberId, CancellationToken ct = default) =>
        GetAsync<AlertPreferencesResponse>(ApiPaths.AlertPreferences(cardiMemberId), ct);

    public Task<AlertPreferencesResponse?> PeekAlertPreferencesAsync(
        Guid cardiMemberId, CancellationToken ct = default) =>
        PeekAsync<AlertPreferencesResponse>(ApiPaths.AlertPreferences(cardiMemberId), ct);

    public async Task<AlertRuleSettingResponse> SetAlertRuleEnabledAsync(
        Guid cardiMemberId, string ruleId, bool enabled, CancellationToken ct = default)
    {
        var setting = await SendAsync<SetAlertRuleEnabledRequest, AlertRuleSettingResponse>(
            HttpMethod.Patch,
            $"api/v1/cardimembers/{cardiMemberId}/alert-preferences/rules/{Uri.EscapeDataString(ruleId)}",
            new SetAlertRuleEnabledRequest { Enabled = enabled },
            ct);
        await EvictAsync(ApiPaths.AlertPreferences(cardiMemberId));
        return setting;
    }

    public Task<AlarmCatalogueResponse> GetAlarmCatalogueAsync(CancellationToken ct = default) =>
        GetAsync<AlarmCatalogueResponse>(ApiPaths.AlarmCatalogue, ct);

    public Task<AlarmCatalogueResponse?> PeekAlarmCatalogueAsync(CancellationToken ct = default) =>
        PeekAsync<AlarmCatalogueResponse>(ApiPaths.AlarmCatalogue, ct);

    public Task<IReadOnlyList<MetricAlarmResponse>> GetMemberAlarmsAsync(
        Guid cardiMemberId, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<MetricAlarmResponse>>(ApiPaths.MemberAlarms(cardiMemberId), ct);

    public Task<IReadOnlyList<MetricAlarmResponse>?> PeekMemberAlarmsAsync(
        Guid cardiMemberId, CancellationToken ct = default) =>
        PeekAsync<IReadOnlyList<MetricAlarmResponse>>(ApiPaths.MemberAlarms(cardiMemberId), ct);

    public async Task<MetricAlarmResponse> CreateMemberAlarmAsync(
        Guid cardiMemberId, SaveMetricAlarmRequest request, CancellationToken ct = default)
    {
        var created = await SendAsync<SaveMetricAlarmRequest, MetricAlarmResponse>(
            HttpMethod.Post, ApiPaths.MemberAlarms(cardiMemberId), request, ct);
        await EvictAsync(ApiPaths.MemberAlarms(cardiMemberId));
        return created;
    }

    public async Task<MetricAlarmResponse> SaveMemberAlarmAsync(
        Guid cardiMemberId, Guid alarmId, SaveMetricAlarmRequest request, CancellationToken ct = default)
    {
        var saved = await SendAsync<SaveMetricAlarmRequest, MetricAlarmResponse>(
            HttpMethod.Put, $"api/v1/cardimembers/{cardiMemberId}/alarms/{alarmId}", request, ct);
        await EvictAsync(ApiPaths.MemberAlarms(cardiMemberId));
        return saved;
    }

    public async Task DeleteMemberAlarmAsync(
        Guid cardiMemberId, Guid alarmId, CancellationToken ct = default)
    {
        await SendNoDataAsync(HttpMethod.Delete, $"api/v1/cardimembers/{cardiMemberId}/alarms/{alarmId}", ct);
        await EvictAsync(ApiPaths.MemberAlarms(cardiMemberId));
    }

    public Task<JournalSettingsResponse> GetJournalSettingsAsync(
        Guid cardiMemberId, CancellationToken ct = default) =>
        GetAsync<JournalSettingsResponse>(ApiPaths.JournalSettings(cardiMemberId), ct);

    public Task<JournalSettingsResponse?> PeekJournalSettingsAsync(
        Guid cardiMemberId, CancellationToken ct = default) =>
        PeekAsync<JournalSettingsResponse>(ApiPaths.JournalSettings(cardiMemberId), ct);

    public async Task<JournalSettingsResponse> UpdateJournalSettingsAsync(
        Guid cardiMemberId, UpdateJournalSettingsRequest request, CancellationToken ct = default)
    {
        var settings = await SendAsync<UpdateJournalSettingsRequest, JournalSettingsResponse>(
            HttpMethod.Put, ApiPaths.JournalSettings(cardiMemberId), request, ct);
        await EvictAsync(ApiPaths.JournalSettings(cardiMemberId));
        return settings;
    }

    public Task<DashboardResponse> GetDashboardAsync(Guid cardiMemberId, CancellationToken ct = default) =>
        GetAsync<DashboardResponse>(ApiPaths.Dashboard(cardiMemberId), ct);

    public Task<DashboardResponse?> PeekDashboardAsync(Guid cardiMemberId, CancellationToken ct = default) =>
        PeekAsync<DashboardResponse>(ApiPaths.Dashboard(cardiMemberId), ct);

    public Task<CurrentStatusMessageResponse> GetCurrentStatusAsync(Guid cardiMemberId, CancellationToken ct = default) =>
        GetAsync<CurrentStatusMessageResponse>(ApiPaths.CurrentStatus(cardiMemberId), ct);

    public Task<DigestResponse> GetDigestAsync(Guid cardiMemberId, CancellationToken ct = default) =>
        GetAsync<DigestResponse>(ApiPaths.Digest(cardiMemberId), ct);

    public Task<DigestResponse?> PeekDigestAsync(Guid cardiMemberId, CancellationToken ct = default) =>
        PeekAsync<DigestResponse>(ApiPaths.Digest(cardiMemberId), ct);

    public Task<AdviseResponse> GetAdviseAsync(Guid cardiMemberId, CancellationToken ct = default) =>
        GetAsync<AdviseResponse>(ApiPaths.Advise(cardiMemberId), ct);

    public Task<AdviseResponse?> PeekAdviseAsync(Guid cardiMemberId, CancellationToken ct = default) =>
        PeekAsync<AdviseResponse>(ApiPaths.Advise(cardiMemberId), ct);

    /// <summary>
    /// The first page of a member's questions as every screen asks for it — the detail screen's
    /// call takes the defaults, and the questionnaires screen's own constant matches them.
    /// </summary>
    private const int DefaultQuestionnairePage = 1;
    private const int DefaultQuestionnairePageSize = 20;

    /// <summary>The keys a change to one member's profile or monitoring state makes stale.</summary>
    private static string[] MemberProfileKeys(Guid cardiMemberId) =>
    [
        ApiPaths.CardiMember(cardiMemberId),
        ApiPaths.Dashboard(cardiMemberId),
        ApiPaths.CardiMembers,
    ];

    /// <summary>
    /// The one call in this client whose answer nobody reads. It returns 202 the moment the API
    /// has noted the arrival, so it needs no special timeout and gets the default: the model load
    /// it may start happens on the server, long after this has returned.
    /// </summary>
    public Task PrepareAssistantAsync(CancellationToken ct = default) =>
        SendNoDataAsync(HttpMethod.Post, "api/v1/assistant/prepare", ct);

    /// <summary>
    /// How long a member-chat send may run before the app hangs up. The clinical read is the one
    /// call in this chain that can't move off the self-hosted MedGemma instance, so this has to
    /// outlast that call's own server-side ceiling — AI:Private:TimeoutSeconds (900s as of the
    /// concurrency fix in cloud_run.tf: a single-instance Ollama admits one request at a time, so
    /// a queued-behind-another-caller generation can legitimately take close to the full 900s
    /// before returning). The previous 180s was set from "observed dev sends run one to two
    /// minutes" before that contention was diagnosed, and could — and did — cut a caller off
    /// while the server was still working: giving up here doesn't stop the generation, it just
    /// means nobody is listening for the answer it produces. Same "the outer layer must outlast
    /// the inner one by a margin" rule Terraform applies to the Cloud Run request timeout
    /// (medgemma_timeout_seconds + 60s), applied one layer further out.
    /// </summary>
    private static readonly TimeSpan MemberChatSendTimeout = TimeSpan.FromSeconds(960);

    public Task<MemberChatMessageResponse> SendMemberChatMessageAsync(
        Guid cardiMemberId, MemberChatMessageRequest request, CancellationToken ct = default) =>
        SendAsync<MemberChatMessageRequest, MemberChatMessageResponse>(
            HttpMethod.Post, $"api/v1/member-chat/members/{cardiMemberId}/messages", request, ct,
            timeout: MemberChatSendTimeout);

    public Task<MemberChatHistoryResponse?> GetCurrentMemberChatSessionAsync(
        Guid cardiMemberId, CancellationToken ct = default) =>
        GetAsync<MemberChatHistoryResponse?>(
            $"api/v1/member-chat/members/{cardiMemberId}/sessions/current", ct,
            // 200 with a null data is this endpoint's documented "no active session yet" —
            // see MemberChatController.GetCurrentSession — not a malformed reply.
            allowNullData: true);

    public Task<MemberChatSessionListResponse> GetMemberChatSessionsAsync(
        Guid cardiMemberId, CancellationToken ct = default) =>
        GetAsync<MemberChatSessionListResponse>(
            $"api/v1/member-chat/members/{cardiMemberId}/sessions", ct);

    public Task<MemberChatHistoryResponse> GetMemberChatSessionAsync(
        Guid cardiMemberId, Guid sessionId, CancellationToken ct = default) =>
        GetAsync<MemberChatHistoryResponse>(
            $"api/v1/member-chat/members/{cardiMemberId}/sessions/{sessionId}", ct);

    public Task<MemberChatEndSessionResponse> EndCurrentMemberChatSessionAsync(
        Guid cardiMemberId, CancellationToken ct = default) =>
        SendAsync<MemberChatEndSessionResponse>(
            HttpMethod.Post, $"api/v1/member-chat/members/{cardiMemberId}/sessions/current/end", ct);

    public Task<MemberChatHistoryResponse> ContinueMemberChatSessionAsync(
        Guid cardiMemberId, Guid sessionId, CancellationToken ct = default) =>
        SendAsync<MemberChatHistoryResponse>(
            HttpMethod.Post, $"api/v1/member-chat/members/{cardiMemberId}/sessions/{sessionId}/continue", ct);

    public Task<MemberChatDeleteSessionsResponse> DeleteMemberChatSessionsAsync(
        Guid cardiMemberId, IReadOnlyList<Guid> sessionIds, CancellationToken ct = default) =>
        SendAsync<MemberChatDeleteSessionsRequest, MemberChatDeleteSessionsResponse>(
            HttpMethod.Post, $"api/v1/member-chat/members/{cardiMemberId}/sessions/delete",
            new MemberChatDeleteSessionsRequest { SessionIds = [.. sessionIds] }, ct);

    public Task<MemberChatWaitingResponse> GetMemberChatWaitingSentencesAsync(
        Guid cardiMemberId, MemberChatMessageRequest request, CancellationToken ct = default) =>
        SendAsync<MemberChatMessageRequest, MemberChatWaitingResponse>(
            HttpMethod.Post, $"api/v1/member-chat/members/{cardiMemberId}/waiting-sentences", request, ct);

    public Task<MemberChatSuggestionsResponse> GetMemberChatSuggestionsAsync(
        Guid cardiMemberId, CancellationToken ct = default) =>
        GetAsync<MemberChatSuggestionsResponse>(
            $"api/v1/member-chat/members/{cardiMemberId}/suggestions", ct);

    public Task<IReadOnlyList<DigestResponse>> GetJournalEntriesAsync(
        Guid cardiMemberId,
        JournalCadence cadence,
        int limit,
        string? search = null,
        DateOnly? from = null,
        string? urgency = null,
        CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<DigestResponse>>(
            ApiPaths.JournalEntries(cardiMemberId, cadence, limit, search, from, urgency), ct);

    public Task<IReadOnlyList<DigestResponse>?> PeekJournalEntriesAsync(
        Guid cardiMemberId,
        JournalCadence cadence,
        int limit,
        string? search = null,
        DateOnly? from = null,
        string? urgency = null,
        CancellationToken ct = default) =>
        PeekAsync<IReadOnlyList<DigestResponse>>(
            ApiPaths.JournalEntries(cardiMemberId, cadence, limit, search, from, urgency), ct);

    public Task<DigestResponse> GetJournalEntryAsync(
        Guid cardiMemberId,
        JournalCadence cadence,
        DateOnly localDate,
        CancellationToken ct = default) =>
        GetAsync<DigestResponse>(ApiPaths.JournalEntry(cardiMemberId, cadence, localDate), ct);

    public Task<DigestResponse?> PeekJournalEntryAsync(
        Guid cardiMemberId,
        JournalCadence cadence,
        DateOnly localDate,
        CancellationToken ct = default) =>
        PeekAsync<DigestResponse>(ApiPaths.JournalEntry(cardiMemberId, cadence, localDate), ct);

    public Task<QuestionnairesPageResponse> GetQuestionnairesAsync(
        Guid cardiMemberId,
        string? search = null,
        int page = DefaultQuestionnairePage,
        int pageSize = DefaultQuestionnairePageSize,
        CancellationToken ct = default) =>
        GetAsync<QuestionnairesPageResponse>(ApiPaths.Questionnaires(cardiMemberId, search, page, pageSize), ct);

    public Task<QuestionnairesPageResponse?> PeekQuestionnairesAsync(
        Guid cardiMemberId,
        string? search = null,
        int page = DefaultQuestionnairePage,
        int pageSize = DefaultQuestionnairePageSize,
        CancellationToken ct = default) =>
        PeekAsync<QuestionnairesPageResponse>(ApiPaths.Questionnaires(cardiMemberId, search, page, pageSize), ct);

    public async Task<QuestionnaireResponse> AnswerQuestionnaireAsync(
        Guid questionnaireId, AnswerQuestionnaireRequest request, CancellationToken ct = default)
    {
        var answered = await SendAsync<AnswerQuestionnaireRequest, QuestionnaireResponse>(
            HttpMethod.Put, $"api/v1/questionnaires/{questionnaireId}/answer", request, ct);
        await EvictAsync(QuestionnaireKeys(answered));
        return answered;
    }

    public async Task<QuestionnaireResponse> DismissQuestionnaireAsync(
        Guid questionnaireId, CancellationToken ct = default)
    {
        var dismissed = await SendAsync<QuestionnaireResponse>(
            HttpMethod.Put, $"api/v1/questionnaires/{questionnaireId}/dismiss", ct);
        await EvictAsync(QuestionnaireKeys(dismissed));
        return dismissed;
    }

    public async Task<QuestionnaireResponse> ExpireQuestionnaireAsync(
        Guid questionnaireId, CancellationToken ct = default)
    {
        var expired = await SendAsync<QuestionnaireResponse>(
            HttpMethod.Put, $"api/v1/questionnaires/{questionnaireId}/expire", ct);
        await EvictAsync(QuestionnaireKeys(expired));
        return expired;
    }

    /// <summary>
    /// A question answered, skipped or retired changes the member's first page of questions and
    /// the dashboard and detail cards that surface the pending one. The member id comes off the
    /// response, since the call itself only knows the question.
    /// </summary>
    private static string[] QuestionnaireKeys(QuestionnaireResponse questionnaire) =>
    [
        ApiPaths.Questionnaires(questionnaire.CardiMemberId, null, DefaultQuestionnairePage, DefaultQuestionnairePageSize),
        ApiPaths.Dashboard(questionnaire.CardiMemberId),
        ApiPaths.CardiMember(questionnaire.CardiMemberId),
    ];

    public Task DeleteQuestionnaireAsync(Guid questionnaireId, CancellationToken ct = default) =>
        SendNoDataAsync(HttpMethod.Delete, $"api/v1/questionnaires/{questionnaireId}", ct);

    public Task<AlertListResponse> GetAlertsAsync(
        string? severity = null,
        string? status = null,
        DateTime? from = null,
        DateTime? to = null,
        int? limit = null,
        Guid? cardiMemberId = null,
        CancellationToken ct = default) =>
        GetAsync<AlertListResponse>(ApiPaths.Alerts(severity, status, from, to, limit, cardiMemberId), ct);

    public Task<AlertListResponse?> PeekAlertsAsync(
        string? severity = null,
        string? status = null,
        DateTime? from = null,
        DateTime? to = null,
        int? limit = null,
        Guid? cardiMemberId = null,
        CancellationToken ct = default) =>
        // The same key the live call writes under, so a peek can only ever answer the exact
        // question the screen is about to ask the API — never a neighbouring filter's page.
        PeekAsync<AlertListResponse>(ApiPaths.Alerts(severity, status, from, to, limit, cardiMemberId), ct);

    public Task<AlertDetailResponse> GetAlertAsync(Guid alertId, CancellationToken ct = default) =>
        GetAsync<AlertDetailResponse>(ApiPaths.Alert(alertId), ct);

    public Task<AlertDetailResponse?> PeekAlertAsync(Guid alertId, CancellationToken ct = default) =>
        PeekAsync<AlertDetailResponse>(ApiPaths.Alert(alertId), ct);

    // Alert mutations evict the detail only. The list keys are parameterised by filter and the
    // dashboard by a member id these calls do not have; both screens re-fetch live on every
    // landing and tick, and the alerts list already masks the gap with its pending-deletes set.
    public async Task<AlertAcknowledgementResponse> AcknowledgeAlertAsync(
        Guid alertId, CancellationToken ct = default)
    {
        var acknowledged = await SendAsync<AlertAcknowledgementResponse>(
            HttpMethod.Post, $"api/v1/alerts/{alertId}/acknowledge", ct);
        await EvictAsync(ApiPaths.Alert(alertId));
        return acknowledged;
    }

    public async Task<AlertAcknowledgementResponse> UnacknowledgeAlertAsync(
        Guid alertId, CancellationToken ct = default)
    {
        var unacknowledged = await SendAsync<AlertAcknowledgementResponse>(
            HttpMethod.Delete, $"api/v1/alerts/{alertId}/acknowledge", ct);
        await EvictAsync(ApiPaths.Alert(alertId));
        return unacknowledged;
    }

    public async Task DeleteAlertAsync(Guid alertId, CancellationToken ct = default)
    {
        await SendNoDataAsync(HttpMethod.Delete, ApiPaths.Alert(alertId), ct);
        await EvictAsync(ApiPaths.Alert(alertId));
    }

    public Task<DeviceListResponse> GetDevicesAsync(Guid cardiMemberId, CancellationToken ct = default) =>
        GetAsync<DeviceListResponse>(ApiPaths.Devices(cardiMemberId), ct);

    public Task<DeviceListResponse?> PeekDevicesAsync(Guid cardiMemberId, CancellationToken ct = default) =>
        PeekAsync<DeviceListResponse>(ApiPaths.Devices(cardiMemberId), ct);

    public async Task DisconnectDeviceAsync(Guid cardiMemberId, Guid deviceId, CancellationToken ct = default)
    {
        await SendNoDataAsync(HttpMethod.Delete, $"api/v1/cardimembers/{cardiMemberId}/devices/{deviceId}", ct);
        await EvictAsync(DeviceKeys(cardiMemberId));
    }

    public async Task<DeviceResponse> SetPrimaryDeviceAsync(
        Guid cardiMemberId, Guid deviceId, CancellationToken ct = default)
    {
        var device = await SendAsync<DeviceResponse>(
            HttpMethod.Post, $"api/v1/cardimembers/{cardiMemberId}/devices/{deviceId}/primary", ct);
        await EvictAsync(DeviceKeys(cardiMemberId));
        return device;
    }

    public async Task<DeviceResponse> RefreshDeviceConnectionAsync(
        Guid cardiMemberId, Guid deviceId, CancellationToken ct = default)
    {
        var device = await SendAsync<DeviceResponse>(
            HttpMethod.Post, $"api/v1/cardimembers/{cardiMemberId}/devices/{deviceId}/refresh", ct);
        await EvictAsync(DeviceKeys(cardiMemberId));
        return device;
    }

    public async Task<DeviceSyncResultResponse> SyncDevicesAsync(
        Guid cardiMemberId, CancellationToken ct = default)
    {
        var result = await SendAsync<DeviceSyncResultResponse>(
            HttpMethod.Post, $"api/v1/cardimembers/{cardiMemberId}/devices/sync", ct);
        await EvictAsync(DeviceKeys(cardiMemberId));
        return result;
    }

    public async Task<DeviceHistoryRepullResponse> RequestHistoryRepullAsync(
        Guid cardiMemberId, Guid deviceId, int days, CancellationToken ct = default)
    {
        var repull = await PostAsync<HistoryRepullRequest, DeviceHistoryRepullResponse>(
            $"api/v1/cardimembers/{cardiMemberId}/devices/{deviceId}/history-repull",
            new HistoryRepullRequest { Days = days },
            ct);
        // The queued request is part of the device list's own payload (historyRepull), so a
        // cached list would still show the action on offer moments after it was taken.
        await EvictAsync(DeviceKeys(cardiMemberId));
        return repull;
    }

    /// <summary>What a device coming, going or syncing makes stale: the device list and the two reads that quote it.</summary>
    private static string[] DeviceKeys(Guid cardiMemberId) =>
    [
        ApiPaths.Devices(cardiMemberId),
        ApiPaths.Dashboard(cardiMemberId),
        ApiPaths.CardiMember(cardiMemberId),
    ];

    public Task<OAuthInitiationResponse> InitiateDeviceConnectionAsync(Guid cardiMemberId, ConnectDeviceRequest request, CancellationToken ct = default) =>
        PostAsync<ConnectDeviceRequest, OAuthInitiationResponse>($"api/v1/cardimembers/{cardiMemberId}/devices", request, ct);

    public Task<DeviceResponse> CompleteDeviceConnectionAsync(string provider, OAuthCallbackRequest request, CancellationToken ct = default) =>
        PostAsync<OAuthCallbackRequest, DeviceResponse>($"api/v1/oauth/callback/{provider}", request, ct);

    public Task ResendVerificationAsync(string email, CancellationToken ct = default) =>
        PostAsync<ResendVerificationRequest, bool>(
            "api/v1/auth/resend-verification", new ResendVerificationRequest { Email = email }, ct);

    // ---- Data-completeness notifications ----

    public Task<NotificationListResponse> GetNotificationsAsync(
        string? state = null,
        string? category = null,
        bool? owned = null,
        int? limit = null,
        CancellationToken ct = default) =>
        GetAsync<NotificationListResponse>(ApiPaths.Notifications(state, category, owned, limit), ct);

    public Task<NotificationListResponse?> PeekNotificationsAsync(
        string? state = null,
        string? category = null,
        bool? owned = null,
        int? limit = null,
        CancellationToken ct = default) =>
        PeekAsync<NotificationListResponse>(ApiPaths.Notifications(state, category, owned, limit), ct);

    public Task<NotificationSummaryResponse> GetNotificationSummaryAsync(CancellationToken ct = default) =>
        GetAsync<NotificationSummaryResponse>(ApiPaths.NotificationSummary, ct);

    public Task<NotificationSummaryResponse?> PeekNotificationSummaryAsync(CancellationToken ct = default) =>
        PeekAsync<NotificationSummaryResponse>(ApiPaths.NotificationSummary, ct);

    public async Task MarkNotificationSeenAsync(Guid notificationId, CancellationToken ct = default)
    {
        await SendNoDataAsync(HttpMethod.Post, $"api/v1/notifications/{notificationId}/seen", ct);
        await EvictAsync(NotificationKeys);
    }

    public async Task<NotificationResponse> SnoozeNotificationAsync(
        Guid notificationId, TimeSpan? duration = null, CancellationToken ct = default)
    {
        var snoozed = await PostAsync<SnoozeNotificationBody, NotificationResponse>(
            $"api/v1/notifications/{notificationId}/snooze",
            // Omitted rather than zero: the server falls back to the rule's own default, which is
            // the right answer when the user taps "not now" without picking a length.
            new SnoozeNotificationBody { Duration = duration?.ToString("c") },
            ct);
        await EvictAsync(NotificationKeys);
        return snoozed;
    }

    public async Task DismissNotificationAsync(
        Guid notificationId, bool acknowledgedConsequence = false, CancellationToken ct = default)
    {
        await SendNoDataAsync(
            HttpMethod.Post, $"api/v1/notifications/{notificationId}/dismiss",
            new DismissNotificationBody { AcknowledgedConsequence = acknowledgedConsequence },
            ct);
        await EvictAsync(NotificationKeys);
    }

    public Task<List<NotificationMuteResponse>> GetNotificationMutesAsync(CancellationToken ct = default) =>
        GetAsync<List<NotificationMuteResponse>>(ApiPaths.NotificationMutes, ct);

    public Task<List<NotificationMuteResponse>?> PeekNotificationMutesAsync(CancellationToken ct = default) =>
        PeekAsync<List<NotificationMuteResponse>>(ApiPaths.NotificationMutes, ct);

    public async Task RemoveNotificationMuteAsync(Guid muteId, CancellationToken ct = default)
    {
        await SendNoDataAsync(HttpMethod.Delete, $"api/v1/notifications/mutes/{muteId}", ct);
        await EvictAsync(NotificationKeys);
    }

    public async Task ResetNotificationMutesAsync(CancellationToken ct = default)
    {
        await SendNoDataAsync(HttpMethod.Post, "api/v1/notifications/mutes/reset", ct);
        await EvictAsync(NotificationKeys);
    }

    /// <summary>
    /// What any change to a notification makes stale: the open inbox (the only list the app
    /// reads — NotificationsPage asks for state=Open), the badge summary, and the mutes a
    /// snooze or dismissal may have added to.
    /// </summary>
    private static readonly string[] NotificationKeys =
    [
        ApiPaths.Notifications("Open", null, null, null),
        ApiPaths.NotificationSummary,
        ApiPaths.NotificationMutes,
    ];

    public Task UpdateTimeZoneAsync(string timeZoneId, CancellationToken ct = default) =>
        SendNoDataAsync(
            HttpMethod.Put, "api/v1/users/me/timezone",
            new UpdateTimeZoneBody { TimeZoneId = timeZoneId }, ct);

    public Task<PushDeviceTokenResponse> RegisterPushDeviceAsync(
        RegisterPushDeviceRequest request, CancellationToken ct = default) =>
        PostAsync<RegisterPushDeviceRequest, PushDeviceTokenResponse>("api/v1/notifications/devices", request, ct);

    public Task UnregisterPushDeviceAsync(string deviceId, CancellationToken ct = default) =>
        SendNoDataAsync(
            HttpMethod.Delete, "api/v1/notifications/devices",
            new UnregisterPushDeviceRequest { DeviceId = deviceId }, ct);

    // The background push handler's ack. A successful ack that throws here reads as a failed
    // delivery, which is exactly what escalation keys off (§7.2 C3) — so this one must not
    // trip over the message-only envelope the endpoint returns.
    public Task AckDeliveredAsync(Guid deliveryId, string ackToken, CancellationToken ct = default) =>
        SendNoDataAsync(
            HttpMethod.Post, $"api/v1/notifications/{deliveryId}/delivered",
            new AckDeliveryRequest { AckToken = ackToken }, ct);

    public Task<NotificationPreferenceResponse> GetNotificationPreferencesAsync(CancellationToken ct = default) =>
        GetAsync<NotificationPreferenceResponse>(ApiPaths.NotificationPreferences, ct);

    public Task<NotificationPreferenceResponse?> PeekNotificationPreferencesAsync(CancellationToken ct = default) =>
        PeekAsync<NotificationPreferenceResponse>(ApiPaths.NotificationPreferences, ct);

    public async Task<NotificationPreferenceResponse> UpdateNotificationPreferencesAsync(
        UpdateNotificationPreferenceRequest request, CancellationToken ct = default)
    {
        var preferences = await SendAsync<UpdateNotificationPreferenceRequest, NotificationPreferenceResponse>(
            HttpMethod.Put, ApiPaths.NotificationPreferences, request, ct);
        await EvictAsync(ApiPaths.NotificationPreferences);
        return preferences;
    }

    private sealed class SnoozeNotificationBody
    {
        public string? Duration { get; set; }
    }

    private sealed class DismissNotificationBody
    {
        public bool AcknowledgedConsequence { get; set; }
    }

    private sealed class UpdateTimeZoneBody
    {
        public string? TimeZoneId { get; set; }
    }

    public CacheOrigin? OriginOf(Task call)
    {
        ArgumentNullException.ThrowIfNull(call);
        return _origins.TryGetValue(call, out var origin) ? origin : null;
    }

    /// <summary>
    /// Starts the call and files its origin under the task it returns, before handing that same
    /// task back. Not itself async: the task has to exist before it can be a key, which it cannot
    /// inside the method that produces it.
    /// </summary>
    private Task<T> GetAsync<T>(string path, CancellationToken ct, bool allowNullData = false)
    {
        var origin = new CacheOrigin();
        var call = GetCoreAsync<T>(path, origin, allowNullData, ct);
        _origins.AddOrUpdate(call, origin);
        return call;
    }

    /// <summary>
    /// The cache-only twin of <see cref="GetAsync{T}"/>: the device's last answer to exactly
    /// this path, or null, without going near the network. Its origin is filed the same way, so
    /// <see cref="OriginOf"/> on the returned task says when the snapshot was saved — which is
    /// what a screen showing it needs to say out loud.
    /// </summary>
    private Task<T?> PeekAsync<T>(string path, CancellationToken ct)
    {
        var origin = new CacheOrigin();
        var call = TryReadCacheAsync<T>(path, origin, ct);
        _origins.AddOrUpdate(call, origin);
        return call;
    }

    private async Task<T> GetCoreAsync<T>(string path, CacheOrigin origin, bool allowNullData, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync(path, ct);
        }
        catch (Exception ex) when (IsTransport(ex))
        {
            if (ex is OperationCanceledException && ct.IsCancellationRequested)
                throw NetworkError("GET", path, ex, ct);

            if (await TryReadCacheAsync<T>(path, origin, ct) is { } cached)
                return cached;

            throw NetworkError("GET", path, ex, ct);
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            // The server says it is gone. A snapshot that outlived it would be served on the
            // next landing as if it were still there — a removed member's dashboard, a deleted
            // alert — so the device forgets it in the same breath.
            await EvictAsync(path);
            throw await MapErrorAsync("GET", path, response, ct);
        }

        if (!response.IsSuccessStatusCode)
            throw await MapErrorAsync("GET", path, response, ct);

        var body = await response.Content.ReadAsStringAsync(ct);
        var value = UnwrapEnvelope<T>("GET", path, body, response.StatusCode, allowNullData);
        // A null-data success is an answer, but not one worth caching: TryReadCacheAsync would
        // only reject the entry as unreadable on the way back out, one warning per offline read.
        if (value is not null)
            await TrySaveCacheAsync(path, body, ct);
        return value;
    }

    private async Task<TResponse> PostAsync<TRequest, TResponse>(string path, TRequest body, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsJsonAsync(path, body, Json, ct);
        }
        catch (Exception ex) when (IsTransport(ex))
        {
            throw NetworkError("POST", path, ex, ct);
        }
        return await ReadEnvelopeAsync<TResponse>("POST", path, response, ct);
    }

    /// <summary>PUT/DELETE/bodyless-POST returning the standard envelope.</summary>
    private Task<TResponse> SendAsync<TResponse>(HttpMethod method, string path, CancellationToken ct) =>
        SendAsync<object?, TResponse>(method, path, body: null, ct);

    private async Task<TResponse> SendAsync<TRequest, TResponse>(
        HttpMethod method, string path, TRequest? body, CancellationToken ct, TimeSpan? timeout = null)
    {
        var response = await SendCoreAsync(method, path, body, ct, timeout);
        return await ReadEnvelopeAsync<TResponse>(method.Method, path, response, ct);
    }

    /// <summary>
    /// For commands whose success is the status code: 204s, and the message-only 200 envelope
    /// (<c>{ success, message, timestamp }</c> with no <c>data</c>) the API returns from a command
    /// that has nothing to hand back. There is no payload to unwrap, so reading one would only
    /// invent failures — <see cref="ReadEnvelopeAsync"/> rejects a null <c>data</c>, which is the
    /// right call for endpoints that do return something and the wrong one here.
    /// Failures still surface: any non-2xx goes through <see cref="MapErrorAsync"/> as usual.
    /// </summary>
    private async Task SendNoDataAsync<TRequest>(
        HttpMethod method, string path, TRequest? body, CancellationToken ct)
    {
        var response = await SendCoreAsync(method, path, body, ct);
        if (!response.IsSuccessStatusCode)
            throw await MapErrorAsync(method.Method, path, response, ct);
    }

    private Task SendNoDataAsync(HttpMethod method, string path, CancellationToken ct) =>
        SendNoDataAsync<object?>(method, path, body: null, ct);

    private async Task<HttpResponseMessage> SendCoreAsync<TRequest>(
        HttpMethod method, string path, TRequest? body, CancellationToken ct, TimeSpan? timeout = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (timeout is { } perRequest)
            request.Options.Set(TimeoutHandler.TimeoutOption, perRequest);
        if (body is not null)
        {
            // JsonContent re-serializes on each read, so the auth handler's 401 retry can
            // re-send this request — same reason PostAsJsonAsync is used above.
            request.Content = JsonContent.Create(body, mediaType: null, Json);
        }

        try
        {
            return await _http.SendAsync(request, ct);
        }
        catch (Exception ex) when (IsTransport(ex))
        {
            throw NetworkError(method.Method, path, ex, ct);
        }
    }

    private async Task<T> ReadEnvelopeAsync<T>(string method, string path, HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
            throw await MapErrorAsync(method, path, response, ct);

        var body = await response.Content.ReadAsStringAsync(ct);
        return UnwrapEnvelope<T>(method, path, body, response.StatusCode);
    }

    private T UnwrapEnvelope<T>(string method, string path, string body, HttpStatusCode statusCode, bool allowNullData = false)
    {
        var parsed = JsonUtility.TryDeserialize<ApiResponse<T>>(body, out var envelope, out var jsonErrors);
        if (!parsed || envelope!.Data is null)
        {
            // Some endpoints answer a question with "there isn't one" as a successful envelope
            // whose data is null — e.g. member-chat's sessions/current when no conversation
            // exists yet. For those callers a readable success envelope with no data is an
            // answer, not a fault; an unreadable body still is one.
            if (allowNullData && parsed && envelope!.Success)
                return default!;

            _logger.LogError("API {Method} {Path} returned {StatusCode} with an empty or unreadable envelope: {JsonErrors}. Payload: {Payload}",
                method, path, (int)statusCode,
                jsonErrors.Count == 0 ? "no data in envelope" : string.Join("; ", jsonErrors),
                JsonUtility.PreviewOf(body));
            throw new ApiException(statusCode, "The server returned an empty response.");
        }
        return envelope.Data;
    }

    private async Task<T?> TryReadCacheAsync<T>(string path, CacheOrigin origin, CancellationToken ct)
    {
        if (_cache is null)
            return default;

        OfflineCacheEntry? entry;
        try
        {
            entry = await _cache.TryGetAsync(path, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller gave up on this read — a chip tap superseding a load, a screen going
            // away. That is not a failed cache and must not be logged as one; the caller's own
            // cancellation handling is the right place for it to land.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Offline cache read failed for GET {Path}", path);
            return default;
        }

        if (entry is null)
            return default;

        if (!JsonUtility.TryDeserialize<ApiResponse<T>>(entry.Payload, out var envelope, out _)
            || envelope!.Data is null)
        {
            _logger.LogWarning("Offline cache entry for GET {Path} was unreadable; ignoring it", path);
            return default;
        }

        origin.CachedAt = entry.CachedAt;
        _logger.LogInformation("Serving GET {Path} from the on-device cache (saved {CachedAt:o})",
            path, entry.CachedAt);
        return envelope.Data;
    }

    private async Task TrySaveCacheAsync(string path, string body, CancellationToken ct)
    {
        if (_cache is null)
            return;

        try
        {
            await _cache.SaveAsync(path, body, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Offline cache write failed for GET {Path}", path);
        }
    }

    /// <summary>
    /// Drops the snapshots a successful mutation has made stale. Best-effort and uncancellable:
    /// the server has already changed, so a screen's cancel after the fact must not leave the
    /// device holding the old answer, and a cache that cannot delete must not turn a mutation
    /// that succeeded into one that appears to have failed.
    /// </summary>
    private async Task EvictAsync(params string[] keys)
    {
        if (_cache is null)
            return;

        foreach (var key in keys)
        {
            try
            {
                await _cache.RemoveAsync(key, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Offline cache eviction failed for GET {Path}", key);
            }
        }
    }

    // ---- Health data export (M1-17, Story 6.3) ----

    public Task<ReportQueuedResponse> GenerateReportAsync(
        GenerateReportRequest request, CancellationToken ct = default) =>
        PostAsync<GenerateReportRequest, ReportQueuedResponse>("api/v1/reports", request, ct);

    public async Task<ReportStatusResponse?> GetReportStatusAsync(
        string reportId, CancellationToken ct = default)
    {
        try
        {
            return await GetAsync<ReportStatusResponse>($"api/v1/reports/{reportId}", ct);
        }
        catch (ApiException ex) when (ex.IsNotFound)
        {
            // Unknown, expired, or another user's — indistinguishable by design, and all three
            // mean the same thing to a poller: there is nothing here any more.
            return null;
        }
    }

    public async Task<ReportFile> DownloadReportAsync(string reportId, CancellationToken ct = default)
    {
        const string path = "api/v1/reports/{0}/download";
        var requestPath = string.Format(CultureInfo.InvariantCulture, path, reportId);

        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync(requestPath, ct);
        }
        catch (Exception ex) when (IsTransport(ex))
        {
            throw NetworkError("GET", requestPath, ex, ct);
        }

        // Disposed, unlike the JSON calls above: this is the one response in the client that
        // carries a whole file, so holding its content stream holds a pooled connection open for
        // as long as the caller keeps the ReportFile.
        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw await MapErrorAsync("GET", requestPath, response, ct);

            // Raw bytes, not the JSON envelope every other call unwraps — this endpoint serves a
            // file. The filename comes from Content-Disposition because the server built it from
            // the member and period, and the client should not try to reconstruct that.
            var content = await response.Content.ReadAsByteArrayAsync(ct);

            // ToString(), not MediaType: the header is "text/csv; charset=utf-8" and MediaType
            // drops the charset — the very thing the CSV renderer's BOM exists to get right. This
            // value is handed to the OS share sheet, so it should say everything the server said.
            var contentType = response.Content.Headers.ContentType?.ToString()
                ?? "application/octet-stream";

            var fileName = response.Content.Headers.ContentDisposition?.FileNameStar
                ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"')
                ?? $"carditrack-export-{reportId}";

            return new ReportFile(content, contentType, fileName);
        }
    }

    private async Task<ApiException> MapErrorAsync(string method, string path, HttpResponseMessage response, CancellationToken ct)
    {
        string message = $"Request failed ({(int)response.StatusCode}).";
        string? traceId = null;
        List<string>? errors = null;
        var body = await response.Content.ReadAsStringAsync(ct);
        if (JsonUtility.TryDeserialize<ErrorResponse>(body, out var error, out var bodyJsonErrors))
        {
            if (!string.IsNullOrWhiteSpace(error!.Message))
                message = error.Message;
            traceId = error.TraceId;
            if (error.Errors is { Count: > 0 })
                errors = error.Errors.Select(e => $"{e.Field}: {e.Message}".TrimStart(' ', ':')).ToList();
        }
        else
        {
            _logger.LogDebug("API {Method} {Path} error body was not a parseable ErrorResponse: {JsonErrors}. Payload: {Payload}",
                method, path, string.Join("; ", bodyJsonErrors), JsonUtility.PreviewOf(body));
        }

        // TraceId ties this entry to the server-side Serilog entry for the same request.
        var level = (int)response.StatusCode >= 500 ? LogLevel.Error : LogLevel.Warning;
        _logger.Log(level, "API {Method} {Path} failed with {StatusCode}: {ServerMessage} (TraceId: {TraceId})",
            method, path, (int)response.StatusCode, message, traceId);

        return new ApiException(response.StatusCode, message, errors);
    }

    private static bool IsTransport(Exception ex) =>
        ex is HttpRequestException or TaskCanceledException or OperationCanceledException or TimeoutException;

    private ApiException NetworkError(string method, string path, Exception ex, CancellationToken ct)
    {
        if (ex is OperationCanceledException && ct.IsCancellationRequested)
            _logger.LogDebug("API {Method} {Path} was canceled by the caller", method, path);
        else
            _logger.LogError(ex, "API {Method} {Path} failed with a transport error", method, path);

        // TimeoutHandler's expiry means the server was reached but too slow — "check your
        // internet" would send the caregiver chasing the wrong problem.
        return ex is TimeoutException
            ? new(HttpStatusCode.RequestTimeout, "The server is taking too long to answer. Try again in a moment.", inner: ex)
            : new(HttpStatusCode.ServiceUnavailable, "No connection. Check your internet and try again.", inner: ex);
    }
}
