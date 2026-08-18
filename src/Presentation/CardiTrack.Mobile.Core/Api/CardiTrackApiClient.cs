using System.Net;
using System.Net.Http.Json;
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

    public bool LastGetWasCached { get; private set; }
    public DateTimeOffset? LastCachedAt { get; private set; }

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
        GetAsync<List<CardiMemberResponse>>("api/Onboarding/cardimembers", ct);

    public Task<CardiMemberDetailResponse> GetCardiMemberAsync(Guid cardiMemberId, CancellationToken ct = default) =>
        GetAsync<CardiMemberDetailResponse>($"api/v1/cardimembers/{cardiMemberId}", ct);

    public Task<CardiMemberDetailResponse> UpdateCardiMemberAsync(
        Guid cardiMemberId, UpdateCardiMemberRequest request, CancellationToken ct = default) =>
        SendAsync<UpdateCardiMemberRequest, CardiMemberDetailResponse>(
            HttpMethod.Put, $"api/v1/cardimembers/{cardiMemberId}", request, ct);

    public Task RemoveCardiMemberAsync(Guid cardiMemberId, CancellationToken ct = default) =>
        SendNoDataAsync(HttpMethod.Delete, $"api/v1/cardimembers/{cardiMemberId}", ct);

    public Task<MonitoringPauseResponse> PauseMonitoringAsync(
        Guid cardiMemberId, PauseMonitoringRequest request, CancellationToken ct = default) =>
        PostAsync<PauseMonitoringRequest, MonitoringPauseResponse>(
            $"api/v1/cardimembers/{cardiMemberId}/pause", request, ct);

    public Task<MonitoringPauseResponse> ResumeMonitoringAsync(
        Guid cardiMemberId, CancellationToken ct = default) =>
        SendAsync<MonitoringPauseResponse>(
            HttpMethod.Delete, $"api/v1/cardimembers/{cardiMemberId}/pause", ct);

    public Task<AlertPreferencesResponse> GetAlertPreferencesAsync(
        Guid cardiMemberId, CancellationToken ct = default) =>
        GetAsync<AlertPreferencesResponse>($"api/v1/cardimembers/{cardiMemberId}/alert-preferences", ct);

    public Task<AlertRuleSettingResponse> SetAlertRuleEnabledAsync(
        Guid cardiMemberId, string ruleId, bool enabled, CancellationToken ct = default) =>
        SendAsync<SetAlertRuleEnabledRequest, AlertRuleSettingResponse>(
            HttpMethod.Patch,
            $"api/v1/cardimembers/{cardiMemberId}/alert-preferences/rules/{Uri.EscapeDataString(ruleId)}",
            new SetAlertRuleEnabledRequest { Enabled = enabled },
            ct);

    public Task<DashboardResponse> GetDashboardAsync(Guid cardiMemberId, CancellationToken ct = default) =>
        GetAsync<DashboardResponse>($"api/v1/cardimembers/{cardiMemberId}/dashboard", ct);

    public Task<CurrentStatusMessageResponse> GetCurrentStatusAsync(Guid cardiMemberId, CancellationToken ct = default) =>
        GetAsync<CurrentStatusMessageResponse>($"api/v1/insights/members/{cardiMemberId}/status", ct);

    public Task<DigestResponse> GetDigestAsync(Guid cardiMemberId, CancellationToken ct = default) =>
        GetAsync<DigestResponse>($"api/v1/insights/members/{cardiMemberId}/digest", ct);

    public Task<IReadOnlyList<DigestResponse>> GetDaybooksAsync(
        Guid cardiMemberId,
        int limit,
        string? search = null,
        DateOnly? from = null,
        string? urgency = null,
        CancellationToken ct = default)
    {
        var query = $"?limit={limit}&audience=daybook";
        if (!string.IsNullOrWhiteSpace(search))
            query += $"&search={Uri.EscapeDataString(search.Trim())}";
        if (from is { } fromDay)
            query += $"&from={fromDay:yyyy-MM-dd}";
        if (!string.IsNullOrWhiteSpace(urgency))
            query += $"&urgency={Uri.EscapeDataString(urgency)}";

        return GetAsync<IReadOnlyList<DigestResponse>>(
            $"api/v1/insights/members/{cardiMemberId}/digests{query}", ct);
    }

    public Task<DigestResponse> GetDaybookAsync(
        Guid cardiMemberId, DateOnly localDate, CancellationToken ct = default) =>
        GetAsync<DigestResponse>(
            $"api/v1/insights/members/{cardiMemberId}/digest?date={localDate:yyyy-MM-dd}&audience=daybook", ct);

    public Task<QuestionnairesPageResponse> GetQuestionnairesAsync(
        Guid cardiMemberId,
        string? search = null,
        int page = 1,
        int pageSize = 20,
        CancellationToken ct = default)
    {
        var query = $"?page={page}&pageSize={pageSize}";
        if (!string.IsNullOrWhiteSpace(search))
            query += $"&search={Uri.EscapeDataString(search)}";

        return GetAsync<QuestionnairesPageResponse>(
            $"api/v1/cardimembers/{cardiMemberId}/questionnaires{query}", ct);
    }

    public Task<QuestionnaireResponse> AnswerQuestionnaireAsync(
        Guid questionnaireId, AnswerQuestionnaireRequest request, CancellationToken ct = default) =>
        SendAsync<AnswerQuestionnaireRequest, QuestionnaireResponse>(
            HttpMethod.Put, $"api/v1/questionnaires/{questionnaireId}/answer", request, ct);

    public Task<QuestionnaireResponse> DismissQuestionnaireAsync(
        Guid questionnaireId, CancellationToken ct = default) =>
        SendAsync<QuestionnaireResponse>(
            HttpMethod.Put, $"api/v1/questionnaires/{questionnaireId}/dismiss", ct);

    public Task<QuestionnaireResponse> ExpireQuestionnaireAsync(
        Guid questionnaireId, CancellationToken ct = default) =>
        SendAsync<QuestionnaireResponse>(
            HttpMethod.Put, $"api/v1/questionnaires/{questionnaireId}/expire", ct);

    public Task DeleteQuestionnaireAsync(Guid questionnaireId, CancellationToken ct = default) =>
        SendNoDataAsync(HttpMethod.Delete, $"api/v1/questionnaires/{questionnaireId}", ct);

    public Task<AlertListResponse> GetAlertsAsync(
        string? severity = null,
        string? status = null,
        DateTime? from = null,
        DateTime? to = null,
        int? limit = null,
        CancellationToken ct = default)
    {
        var filters = new List<string>();
        if (!string.IsNullOrWhiteSpace(severity)) filters.Add($"severity={Uri.EscapeDataString(severity)}");
        if (!string.IsNullOrWhiteSpace(status)) filters.Add($"status={Uri.EscapeDataString(status)}");
        // Round-trip ("O") keeps the offset on the wire, so a "Today" filter set on a phone in
        // Lagos isn't reinterpreted as UTC midnight by the server.
        if (from is { } f) filters.Add($"from={Uri.EscapeDataString(f.ToString("O"))}");
        if (to is { } t) filters.Add($"to={Uri.EscapeDataString(t.ToString("O"))}");
        if (limit is { } l) filters.Add($"limit={l}");

        var path = filters.Count == 0 ? "api/v1/alerts" : $"api/v1/alerts?{string.Join("&", filters)}";
        return GetAsync<AlertListResponse>(path, ct);
    }

    public Task<AlertDetailResponse> GetAlertAsync(Guid alertId, CancellationToken ct = default) =>
        GetAsync<AlertDetailResponse>($"api/v1/alerts/{alertId}", ct);

    public Task<AlertAcknowledgementResponse> AcknowledgeAlertAsync(
        Guid alertId, CancellationToken ct = default) =>
        SendAsync<AlertAcknowledgementResponse>(
            HttpMethod.Post, $"api/v1/alerts/{alertId}/acknowledge", ct);

    public Task<AlertAcknowledgementResponse> UnacknowledgeAlertAsync(
        Guid alertId, CancellationToken ct = default) =>
        SendAsync<AlertAcknowledgementResponse>(
            HttpMethod.Delete, $"api/v1/alerts/{alertId}/acknowledge", ct);

    public Task DeleteAlertAsync(Guid alertId, CancellationToken ct = default) =>
        SendNoDataAsync(HttpMethod.Delete, $"api/v1/alerts/{alertId}", ct);

    public Task<DeviceListResponse> GetDevicesAsync(Guid cardiMemberId, CancellationToken ct = default) =>
        GetAsync<DeviceListResponse>($"api/v1/cardimembers/{cardiMemberId}/devices", ct);

    public Task DisconnectDeviceAsync(Guid cardiMemberId, Guid deviceId, CancellationToken ct = default) =>
        SendNoDataAsync(HttpMethod.Delete, $"api/v1/cardimembers/{cardiMemberId}/devices/{deviceId}", ct);

    public Task<DeviceResponse> SetPrimaryDeviceAsync(
        Guid cardiMemberId, Guid deviceId, CancellationToken ct = default) =>
        SendAsync<DeviceResponse>(
            HttpMethod.Post, $"api/v1/cardimembers/{cardiMemberId}/devices/{deviceId}/primary", ct);

    public Task<DeviceResponse> RefreshDeviceConnectionAsync(
        Guid cardiMemberId, Guid deviceId, CancellationToken ct = default) =>
        SendAsync<DeviceResponse>(
            HttpMethod.Post, $"api/v1/cardimembers/{cardiMemberId}/devices/{deviceId}/refresh", ct);

    public Task<DeviceSyncResultResponse> SyncDevicesAsync(
        Guid cardiMemberId, CancellationToken ct = default) =>
        SendAsync<DeviceSyncResultResponse>(
            HttpMethod.Post, $"api/v1/cardimembers/{cardiMemberId}/devices/sync", ct);

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
        CancellationToken ct = default)
    {
        var filters = new List<string>();
        if (!string.IsNullOrWhiteSpace(state)) filters.Add($"state={Uri.EscapeDataString(state)}");
        if (!string.IsNullOrWhiteSpace(category)) filters.Add($"category={Uri.EscapeDataString(category)}");
        if (owned is { } o) filters.Add($"owned={(o ? "true" : "false")}");
        if (limit is { } l) filters.Add($"limit={l}");

        var path = filters.Count == 0
            ? "api/v1/notifications"
            : $"api/v1/notifications?{string.Join("&", filters)}";
        return GetAsync<NotificationListResponse>(path, ct);
    }

    public Task<NotificationSummaryResponse> GetNotificationSummaryAsync(CancellationToken ct = default) =>
        GetAsync<NotificationSummaryResponse>("api/v1/notifications/summary", ct);

    public Task MarkNotificationSeenAsync(Guid notificationId, CancellationToken ct = default) =>
        SendNoDataAsync(HttpMethod.Post, $"api/v1/notifications/{notificationId}/seen", ct);

    public Task<NotificationResponse> SnoozeNotificationAsync(
        Guid notificationId, TimeSpan? duration = null, CancellationToken ct = default) =>
        PostAsync<SnoozeNotificationBody, NotificationResponse>(
            $"api/v1/notifications/{notificationId}/snooze",
            // Omitted rather than zero: the server falls back to the rule's own default, which is
            // the right answer when the user taps "not now" without picking a length.
            new SnoozeNotificationBody { Duration = duration?.ToString("c") },
            ct);

    public Task DismissNotificationAsync(
        Guid notificationId, bool acknowledgedConsequence = false, CancellationToken ct = default) =>
        SendNoDataAsync(
            HttpMethod.Post, $"api/v1/notifications/{notificationId}/dismiss",
            new DismissNotificationBody { AcknowledgedConsequence = acknowledgedConsequence },
            ct);

    public Task<List<NotificationMuteResponse>> GetNotificationMutesAsync(CancellationToken ct = default) =>
        GetAsync<List<NotificationMuteResponse>>("api/v1/notifications/mutes", ct);

    public Task RemoveNotificationMuteAsync(Guid muteId, CancellationToken ct = default) =>
        SendNoDataAsync(HttpMethod.Delete, $"api/v1/notifications/mutes/{muteId}", ct);

    public Task ResetNotificationMutesAsync(CancellationToken ct = default) =>
        SendNoDataAsync(HttpMethod.Post, "api/v1/notifications/mutes/reset", ct);

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
        GetAsync<NotificationPreferenceResponse>("api/v1/notifications/preferences", ct);

    public Task<NotificationPreferenceResponse> UpdateNotificationPreferencesAsync(
        UpdateNotificationPreferenceRequest request, CancellationToken ct = default) =>
        SendAsync<UpdateNotificationPreferenceRequest, NotificationPreferenceResponse>(
            HttpMethod.Put, "api/v1/notifications/preferences", request, ct);

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

    private async Task<T> GetAsync<T>(string path, CancellationToken ct)
    {
        LastGetWasCached = false;
        LastCachedAt = null;

        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync(path, ct);
        }
        catch (Exception ex) when (IsTransport(ex))
        {
            if (ex is OperationCanceledException && ct.IsCancellationRequested)
                throw NetworkError("GET", path, ex, ct);

            if (await TryReadCacheAsync<T>(path, ct) is { } cached)
                return cached;

            throw NetworkError("GET", path, ex, ct);
        }

        if (!response.IsSuccessStatusCode)
            throw await MapErrorAsync("GET", path, response, ct);

        var body = await response.Content.ReadAsStringAsync(ct);
        var value = UnwrapEnvelope<T>("GET", path, body, response.StatusCode);
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
        HttpMethod method, string path, TRequest? body, CancellationToken ct)
    {
        var response = await SendCoreAsync(method, path, body, ct);
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
        HttpMethod method, string path, TRequest? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
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

    private T UnwrapEnvelope<T>(string method, string path, string body, HttpStatusCode statusCode)
    {
        if (!JsonUtility.TryDeserialize<ApiResponse<T>>(body, out var envelope, out var jsonErrors)
            || envelope!.Data is null)
        {
            _logger.LogError("API {Method} {Path} returned {StatusCode} with an empty or unreadable envelope: {JsonErrors}. Payload: {Payload}",
                method, path, (int)statusCode,
                jsonErrors.Count == 0 ? "no data in envelope" : string.Join("; ", jsonErrors),
                JsonUtility.PreviewOf(body));
            throw new ApiException(statusCode, "The server returned an empty response.");
        }
        return envelope.Data;
    }

    private async Task<T?> TryReadCacheAsync<T>(string path, CancellationToken ct)
    {
        if (_cache is null)
            return default;

        OfflineCacheEntry? entry;
        try
        {
            entry = await _cache.TryGetAsync(path, ct);
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

        LastGetWasCached = true;
        LastCachedAt = entry.CachedAt;
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
        ex is HttpRequestException or TaskCanceledException or OperationCanceledException;

    private ApiException NetworkError(string method, string path, Exception ex, CancellationToken ct)
    {
        if (ex is OperationCanceledException && ct.IsCancellationRequested)
            _logger.LogDebug("API {Method} {Path} was canceled by the caller", method, path);
        else
            _logger.LogError(ex, "API {Method} {Path} failed with a transport error", method, path);

        return new(HttpStatusCode.ServiceUnavailable, "No connection. Check your internet and try again.", inner: ex);
    }
}
