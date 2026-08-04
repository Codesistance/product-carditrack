using System.Net.Http.Json;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Shared;
using CardiTrack.Shared.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace CardiTrack.Infrastructure.ExternalClients;

/// <summary>
/// Minimal Auth0 Management API v2 client. Requires the Web/API application to be
/// authorized for the Management API with read:users + update:users (Auth0 runbook).
/// The management token comes from the client-credentials grant and is cached until
/// shortly before expiry.
/// </summary>
public class Auth0ManagementClient : IAuth0ManagementService
{
    private const string TokenCacheKey = "auth0:management-token";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ConfigurationLoader _config;
    private readonly IMemoryCache _cache;
    private readonly ILogger<Auth0ManagementClient> _logger;

    public Auth0ManagementClient(
        IHttpClientFactory httpClientFactory,
        ConfigurationLoader config,
        IMemoryCache cache,
        ILogger<Auth0ManagementClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
        _cache = cache;
        _logger = logger;
    }

    public async Task TrySendVerificationEmailAsync(string email, CancellationToken ct = default)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("Auth0Client");
            var token = await GetManagementTokenAsync(client, ct);
            if (token is null)
                return;

            var userId = await FindUserIdByEmailAsync(client, token, email, ct);
            if (userId is null)
            {
                // Unknown email — deliberately silent (no user enumeration).
                _logger.LogInformation("Verification resend requested for an unknown email");
                return;
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, "api/v2/jobs/verification-email")
            {
                Content = JsonContent.Create(new { user_id = userId }),
            };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            var response = await client.SendAsync(request, ct);

            if (response.IsSuccessStatusCode)
                _logger.LogInformation("Verification email job created for Auth0 user {Auth0UserId}", userId);
            else
                _logger.LogWarning("Verification email job failed with {StatusCode} for Auth0 user {Auth0UserId}",
                    (int)response.StatusCode, userId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Verification email resend failed");
        }
    }

    private async Task<string?> GetManagementTokenAsync(HttpClient client, CancellationToken ct)
    {
        if (_cache.TryGetValue<string>(TokenCacheKey, out var cached))
            return cached;

        var domain = _config.GetRequired(ConfigurationKeys.Auth0.Domain);
        var response = await client.PostAsJsonAsync("oauth/token", new
        {
            grant_type = "client_credentials",
            client_id = _config.GetRequired(ConfigurationKeys.Auth0.ClientId),
            client_secret = _config.GetRequired(ConfigurationKeys.Auth0.ClientSecret),
            audience = $"https://{domain}/api/v2/",
        }, ct);

        var body = await response.Content.ReadAsStringAsync(ct);
        JToken? payload = null;
        IReadOnlyList<JsonError> errors = [];
        if (response.IsSuccessStatusCode)
            JsonUtility.TryParse(body, out payload, out errors);
        var token = payload?.Value<string>("access_token");

        if (string.IsNullOrEmpty(token))
        {
            // Token bodies can carry credentials — report status + parse locations, not content.
            _logger.LogWarning(
                "Auth0 management token request failed with {StatusCode} ({BodyLength} chars): {Detail}. " +
                "Is the application authorized for the Management API (read:users, update:users)?",
                (int)response.StatusCode, body.Length,
                errors.Count > 0 ? string.Join("; ", errors) : "no access_token in payload");
            return null;
        }

        var expiresIn = payload!.Value<int?>("expires_in") ?? 3600;
        _cache.Set(TokenCacheKey, token, TimeSpan.FromSeconds(Math.Max(60, expiresIn - 60)));
        return token;
    }

    private async Task<string?> FindUserIdByEmailAsync(HttpClient client, string token, string email, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"api/v2/users-by-email?email={Uri.EscapeDataString(email.ToLowerInvariant())}");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var response = await client.SendAsync(request, ct);

        var body = await response.Content.ReadAsStringAsync(ct);
        JToken? payload = null;
        IReadOnlyList<JsonError> errors = [];
        if (response.IsSuccessStatusCode)
            JsonUtility.TryParse(body, out payload, out errors);
        if (payload is null)
        {
            _logger.LogWarning("Auth0 users-by-email lookup failed with {StatusCode}: {JsonErrors}",
                (int)response.StatusCode, string.Join("; ", errors));
            return null;
        }

        return payload is JArray users && users.FirstOrDefault() is JObject user
            ? user.Value<string>("user_id")
            : null;
    }
}
