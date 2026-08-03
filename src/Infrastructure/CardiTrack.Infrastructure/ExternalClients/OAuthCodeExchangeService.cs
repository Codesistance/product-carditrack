using System.Net.Http.Headers;
using CardiTrack.Infrastructure.Settings;
using CardiTrack.Shared.Json;

namespace CardiTrack.Infrastructure.ExternalClients;

/// <summary>
/// authorization_code grant against a device provider's token endpoint (the connect-time
/// counterpart of <see cref="OAuthTokenRefreshService"/>). Fitbit requires HTTP Basic client
/// credentials alongside the PKCE verifier; client_id is also sent in the body per RFC 7636.
/// </summary>
public class OAuthCodeExchangeService : IOAuthCodeExchangeService
{
    private readonly IHttpClientFactory _httpClientFactory;

    public OAuthCodeExchangeService(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task<OAuthTokenResult> ExchangeCodeAsync(
        DeviceProviderSettings providerConfig,
        string code,
        string redirectUri,
        string codeVerifier,
        CancellationToken ct = default)
    {
        using var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes($"{providerConfig.ClientId}:{providerConfig.ClientSecret}")));

        var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = providerConfig.ClientId,
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = codeVerifier
        });

        HttpResponseMessage response;
        try
        {
            response = await client.PostAsync(providerConfig.TokenUrl, body, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new OAuthExchangeException(
                $"Token exchange HTTP call to {providerConfig.Provider} failed.", ex);
        }

        var content = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new OAuthExchangeException(
                $"Token exchange returned {(int)response.StatusCode} from {providerConfig.Provider}: {content}");
        }

        if (!JsonUtility.TryParse(content, out var root, out var jsonErrors))
            // A body that failed to parse is not a usable token response, but it can still
            // contain a live token fragment — report length + error locations, not content.
            throw new OAuthExchangeException(
                $"{providerConfig.Provider} token response was not valid JSON " +
                $"({content.Length} chars): {string.Join("; ", jsonErrors)}");

        var accessToken = root!.Value<string>("access_token");
        if (string.IsNullOrEmpty(accessToken))
            throw new OAuthExchangeException($"{providerConfig.Provider} token response missing access_token.");

        var refreshToken = root.Value<string>("refresh_token");
        var expiresIn = root.Value<int?>("expires_in")
            ?? providerConfig.TokenLifetimeHours * 3600;
        var scope = root.Value<string>("scope");
        var providerUserId = root.Value<string>("user_id");

        return new OAuthTokenResult(accessToken, refreshToken, expiresIn, scope, providerUserId);
    }
}
