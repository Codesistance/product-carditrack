using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;

namespace CardiTrack.Mobile.Core.Api;

/// <summary>
/// Typed client over the CardiTrack API's ApiResponse envelope. All request bodies are
/// buffered JSON so the auth handler's 401 retry can re-send them.
/// </summary>
public sealed class CardiTrackApiClient : ICardiTrackApiClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;

    public CardiTrackApiClient(HttpClient http)
    {
        _http = http;
    }

    public Task<OnboardingStatusResponse> GetOnboardingStatusAsync(CancellationToken ct = default) =>
        GetAsync<OnboardingStatusResponse>("api/Onboarding/status", ct);

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

    private async Task<T> GetAsync<T>(string path, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync(path, ct);
        }
        catch (Exception ex) when (IsTransport(ex))
        {
            throw NetworkError(ex);
        }
        return await ReadEnvelopeAsync<T>(response, ct);
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
            throw NetworkError(ex);
        }
        return await ReadEnvelopeAsync<TResponse>(response, ct);
    }

    private static async Task<T> ReadEnvelopeAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
            throw await MapErrorAsync(response, ct);

        var envelope = await response.Content.ReadFromJsonAsync<ApiResponse<T>>(Json, ct);
        if (envelope is null || envelope.Data is null)
            throw new ApiException(response.StatusCode, "The server returned an empty response.");
        return envelope.Data;
    }

    private static async Task<ApiException> MapErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        string message = $"Request failed ({(int)response.StatusCode}).";
        List<string>? errors = null;
        try
        {
            var error = await response.Content.ReadFromJsonAsync<ErrorResponse>(Json, ct);
            if (!string.IsNullOrWhiteSpace(error?.Message))
                message = error.Message;
            if (error?.Errors is { Count: > 0 })
                errors = error.Errors.Select(e => $"{e.Field}: {e.Message}".TrimStart(' ', ':')).ToList();
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
        }
        return new ApiException(response.StatusCode, message, errors);
    }

    private static bool IsTransport(Exception ex) =>
        ex is HttpRequestException or TaskCanceledException or OperationCanceledException;

    private static ApiException NetworkError(Exception ex) =>
        new(HttpStatusCode.ServiceUnavailable, "No connection. Check your internet and try again.", inner: ex);
}
