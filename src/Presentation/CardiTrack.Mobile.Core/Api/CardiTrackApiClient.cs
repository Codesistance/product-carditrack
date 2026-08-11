using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
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
    private readonly ILogger<CardiTrackApiClient> _logger;

    public CardiTrackApiClient(HttpClient http, ILogger<CardiTrackApiClient>? logger = null)
    {
        _http = http;
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
        SendNoContentAsync(HttpMethod.Delete, $"api/v1/cardimembers/{cardiMemberId}", ct);

    public Task<MonitoringPauseResponse> PauseMonitoringAsync(
        Guid cardiMemberId, PauseMonitoringRequest request, CancellationToken ct = default) =>
        PostAsync<PauseMonitoringRequest, MonitoringPauseResponse>(
            $"api/v1/cardimembers/{cardiMemberId}/pause", request, ct);

    public Task<MonitoringPauseResponse> ResumeMonitoringAsync(
        Guid cardiMemberId, CancellationToken ct = default) =>
        SendAsync<MonitoringPauseResponse>(
            HttpMethod.Delete, $"api/v1/cardimembers/{cardiMemberId}/pause", ct);

    public Task<DashboardResponse> GetDashboardAsync(Guid cardiMemberId, CancellationToken ct = default) =>
        GetAsync<DashboardResponse>($"api/v1/cardimembers/{cardiMemberId}/dashboard", ct);

    public Task<CurrentStatusMessageResponse> GetCurrentStatusAsync(Guid cardiMemberId, CancellationToken ct = default) =>
        GetAsync<CurrentStatusMessageResponse>($"api/v1/insights/members/{cardiMemberId}/status", ct);

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

    public Task<AlertAcknowledgementResponse> AcknowledgeAlertAsync(
        Guid alertId, CancellationToken ct = default) =>
        SendAsync<AlertAcknowledgementResponse>(
            HttpMethod.Post, $"api/v1/alerts/{alertId}/acknowledge", ct);

    public Task<DeviceListResponse> GetDevicesAsync(Guid cardiMemberId, CancellationToken ct = default) =>
        GetAsync<DeviceListResponse>($"api/v1/cardimembers/{cardiMemberId}/devices", ct);

    public Task DisconnectDeviceAsync(Guid cardiMemberId, Guid deviceId, CancellationToken ct = default) =>
        SendNoContentAsync(HttpMethod.Delete, $"api/v1/cardimembers/{cardiMemberId}/devices/{deviceId}", ct);

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
        SendAsync<object>(HttpMethod.Post, $"api/v1/notifications/{notificationId}/seen", ct);

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
        PostAsync<DismissNotificationBody, object>(
            $"api/v1/notifications/{notificationId}/dismiss",
            new DismissNotificationBody { AcknowledgedConsequence = acknowledgedConsequence },
            ct);

    public Task<List<NotificationMuteResponse>> GetNotificationMutesAsync(CancellationToken ct = default) =>
        GetAsync<List<NotificationMuteResponse>>("api/v1/notifications/mutes", ct);

    public Task RemoveNotificationMuteAsync(Guid muteId, CancellationToken ct = default) =>
        SendAsync<object>(HttpMethod.Delete, $"api/v1/notifications/mutes/{muteId}", ct);

    public Task ResetNotificationMutesAsync(CancellationToken ct = default) =>
        SendAsync<object>(HttpMethod.Post, "api/v1/notifications/mutes/reset", ct);

    public Task UpdateTimeZoneAsync(string timeZoneId, CancellationToken ct = default) =>
        SendAsync<UpdateTimeZoneBody, object>(
            HttpMethod.Put, "api/v1/users/me/timezone",
            new UpdateTimeZoneBody { TimeZoneId = timeZoneId }, ct);

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
        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync(path, ct);
        }
        catch (Exception ex) when (IsTransport(ex))
        {
            throw NetworkError("GET", path, ex, ct);
        }
        return await ReadEnvelopeAsync<T>("GET", path, response, ct);
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

    /// <summary>For 204 endpoints — success is the status code, there is no envelope to read.</summary>
    private async Task SendNoContentAsync(HttpMethod method, string path, CancellationToken ct)
    {
        var response = await SendCoreAsync<object?>(method, path, body: null, ct);
        if (!response.IsSuccessStatusCode)
            throw await MapErrorAsync(method.Method, path, response, ct);
    }

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
        if (!JsonUtility.TryDeserialize<ApiResponse<T>>(body, out var envelope, out var jsonErrors)
            || envelope!.Data is null)
        {
            _logger.LogError("API {Method} {Path} returned {StatusCode} with an empty or unreadable envelope: {JsonErrors}. Payload: {Payload}",
                method, path, (int)response.StatusCode,
                jsonErrors.Count == 0 ? "no data in envelope" : string.Join("; ", jsonErrors),
                JsonUtility.PreviewOf(body));
            throw new ApiException(response.StatusCode, "The server returned an empty response.");
        }
        return envelope.Data;
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
