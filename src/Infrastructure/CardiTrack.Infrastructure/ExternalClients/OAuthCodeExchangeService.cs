using System.Net.Http.Headers;
using System.Text.Json;
using CardiTrack.Infrastructure.Settings;

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

        using var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;

        var accessToken = root.TryGetProperty("access_token", out var at) ? at.GetString() : null;
        if (string.IsNullOrEmpty(accessToken))
            throw new OAuthExchangeException($"{providerConfig.Provider} token response missing access_token.");

        var refreshToken = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
        var expiresIn = root.TryGetProperty("expires_in", out var exp)
            ? exp.GetInt32()
            : providerConfig.TokenLifetimeHours * 3600;
        var scope = root.TryGetProperty("scope", out var sc) ? sc.GetString() : null;
        var providerUserId = root.TryGetProperty("user_id", out var uid) ? uid.GetString() : null;

        return new OAuthTokenResult(accessToken, refreshToken, expiresIn, scope, providerUserId);
    }
}
