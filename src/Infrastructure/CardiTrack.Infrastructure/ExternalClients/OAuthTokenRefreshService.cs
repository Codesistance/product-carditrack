using System.Net;
using System.Net.Http.Headers;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Security;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Settings;
using CardiTrack.Shared.Json;

namespace CardiTrack.Infrastructure.ExternalClients;

public class OAuthTokenRefreshService : IOAuthTokenRefreshService
{
    private readonly IDeviceConnectionRepository _deviceConnections;
    private readonly IEncryptionService _encryption;
    private readonly IHttpClientFactory _httpClientFactory;

    private static readonly TimeSpan ExpiryBuffer = TimeSpan.FromMinutes(5);

    public OAuthTokenRefreshService(
        IDeviceConnectionRepository deviceConnections,
        IEncryptionService encryption,
        IHttpClientFactory httpClientFactory)
    {
        _deviceConnections = deviceConnections;
        _encryption = encryption;
        _httpClientFactory = httpClientFactory;
    }

    public async Task<string> RefreshIfExpiredAsync(DeviceConnection connection, DeviceProviderSettings providerConfig)
    {
        // Token is still valid — return decrypted access token without refreshing
        if (connection.TokenExpiry.HasValue
            && connection.TokenExpiry.Value > DateTime.UtcNow.Add(ExpiryBuffer)
            && connection.AccessToken is not null)
        {
            return _encryption.Decrypt(connection.AccessToken);
        }

        if (connection.RefreshToken is null)
            throw new InvalidOperationException(
                $"DeviceConnection {connection.Id} has no refresh token stored.");

        var plainRefreshToken = _encryption.Decrypt(connection.RefreshToken);

        using var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes($"{providerConfig.ClientId}:{providerConfig.ClientSecret}")));

        var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = plainRefreshToken
        });

        HttpResponseMessage response;
        try
        {
            response = await client.PostAsync(providerConfig.TokenUrl, body);
        }
        catch (Exception ex)
        {
            // The call never reached the provider, so nothing was said about the grant. Leaving
            // the status alone is what lets the next scheduled sync retry — writing TokenExpired
            // here retired a working connection on a DNS blip and put the app into "reconnect
            // your device" for a device that had never lost authorisation.
            throw new InvalidOperationException(
                $"Token refresh HTTP call failed for DeviceConnection {connection.Id}.", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();

            if (IsGrantRejection(response.StatusCode, errorBody))
                await _deviceConnections.UpdateStatusAsync(connection.Id, ConnectionStatus.TokenExpired);

            throw new InvalidOperationException(
                $"Token refresh returned {(int)response.StatusCode} for DeviceConnection {connection.Id}: {errorBody}");
        }

        var tokenBody = await response.Content.ReadAsStringAsync();
        if (!JsonUtility.TryParse(tokenBody, out var root, out var jsonErrors))
            // A body that failed to parse is not a usable token response, but it can still
            // contain a live token fragment — report length + error locations, not content.
            throw new InvalidOperationException(
                $"Token refresh response for DeviceConnection {connection.Id} was not valid JSON " +
                $"({tokenBody.Length} chars): {string.Join("; ", jsonErrors)}");

        var newAccessToken = root!.Value<string>("access_token")
            ?? throw new InvalidOperationException("Token response missing access_token.");
        var newRefreshToken = root.Value<string>("refresh_token") ?? plainRefreshToken;
        var expiresIn = root.Value<int?>("expires_in")
            ?? providerConfig.TokenLifetimeHours * 3600;
        var newExpiry = DateTime.UtcNow.AddSeconds(expiresIn);

        await _deviceConnections.UpdateTokenAsync(
            connection.Id,
            _encryption.Encrypt(newAccessToken),
            _encryption.Encrypt(newRefreshToken),
            newExpiry);

        return newAccessToken;
    }

    /// <summary>
    /// OAuth error codes that mean this refresh token will never work again — revoked, already
    /// spent, or consent withdrawn — as opposed to the request being wrong.
    /// </summary>
    private static readonly HashSet<string> GrantDeathCodes = new(StringComparer.Ordinal)
    {
        // RFC 6749 §5.2 — the refresh token is invalid, expired or revoked.
        "invalid_grant",
        // RFC 6750 and provider dialects of it; Fitbit returns these for a dead token.
        "invalid_token",
        "expired_token",
        // The resource owner took the authorisation away.
        "access_denied",
    };

    /// <summary>
    /// Whether the provider is refusing this grant rather than merely failing to serve it. Only
    /// a rejection retires the connection, so the cost of a false positive is a working device
    /// dropped out of syncing until the user reconnects it by hand.
    /// </summary>
    /// <remarks>
    /// The status has to be one the token endpoint uses to carry a credential verdict at all:
    /// a 404, 405 or 415 says our URL, method or content type is wrong, which is a deployment
    /// fault and would otherwise retire every connection for the provider at once.
    /// <para>
    /// Status alone is still not enough, because a 400 covers both "your refresh token is dead"
    /// and "your client secret is wrong" — and the second, left to the status, would retire the
    /// whole fleet the first time a secret was rotated badly. So when the body carries an
    /// <c>error</c> code, that decides it; a body we cannot parse, or one with no code, falls
    /// back to the status.
    /// </para>
    /// </remarks>
    private static bool IsGrantRejection(HttpStatusCode status, string errorBody)
    {
        if (status is not (HttpStatusCode.BadRequest
                           or HttpStatusCode.Unauthorized
                           or HttpStatusCode.Forbidden))
        {
            return false;
        }

        if (!JsonUtility.TryParse(errorBody, out var root, out _) || root is null)
            return true;

        var error = root.Value<string>("error");
        return error is null || GrantDeathCodes.Contains(error);
    }
}
