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

    public Task<DashboardResponse> GetDashboardAsync(Guid cardiMemberId, CancellationToken ct = default) =>
        GetAsync<DashboardResponse>($"api/v1/cardimembers/{cardiMemberId}/dashboard", ct);

    public Task<DeviceListResponse> GetDevicesAsync(Guid cardiMemberId, CancellationToken ct = default) =>
        GetAsync<DeviceListResponse>($"api/v1/cardimembers/{cardiMemberId}/devices", ct);

    public Task<OAuthInitiationResponse> InitiateDeviceConnectionAsync(Guid cardiMemberId, ConnectDeviceRequest request, CancellationToken ct = default) =>
        PostAsync<ConnectDeviceRequest, OAuthInitiationResponse>($"api/v1/cardimembers/{cardiMemberId}/devices", request, ct);

    public Task<DeviceResponse> CompleteDeviceConnectionAsync(string provider, OAuthCallbackRequest request, CancellationToken ct = default) =>
        PostAsync<OAuthCallbackRequest, DeviceResponse>($"api/v1/oauth/callback/{provider}", request, ct);

    public Task ResendVerificationAsync(string email, CancellationToken ct = default) =>
        PostAsync<ResendVerificationRequest, bool>(
            "api/v1/auth/resend-verification", new ResendVerificationRequest { Email = email }, ct);

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
