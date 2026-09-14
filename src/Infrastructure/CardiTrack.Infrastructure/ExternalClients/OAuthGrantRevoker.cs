using CardiTrack.Application.Interfaces.Clients;
using CardiTrack.Application.Interfaces.Security;
using CardiTrack.Domain.Entities;
using CardiTrack.Infrastructure.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CardiTrack.Infrastructure.ExternalClients;

/// <inheritdoc cref="IOAuthGrantRevoker"/>
/// <remarks>
/// <para>
/// One request, RFC 7009 shaped: a form-encoded <c>token</c> to the provider's revocation
/// endpoint. The refresh token is sent in preference to the access token because for Google
/// revoking a refresh token takes the whole grant with it, while revoking an access token ends
/// only that one token's hour.
/// </para>
/// <para>
/// <strong>A 400 is usually success.</strong> RFC 7009 §2.2 has the endpoint answer 200 for a
/// token that was already invalid, but Google answers 400 <c>invalid_token</c>, and that is the
/// outcome we wanted: the grant is not live. Treating it as a failure would fill the logs with
/// warnings about grants that are already gone, and the real failures would be lost among them.
/// </para>
/// </remarks>
public class OAuthGrantRevoker : IOAuthGrantRevoker
{
    private readonly IOptions<List<DeviceProviderSettings>> _providers;
    private readonly IEncryptionService _encryption;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OAuthGrantRevoker> _logger;

    public OAuthGrantRevoker(
        IOptions<List<DeviceProviderSettings>> providers,
        IEncryptionService encryption,
        IHttpClientFactory httpClientFactory,
        ILogger<OAuthGrantRevoker> logger)
    {
        _providers = providers;
        _encryption = encryption;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<bool> TryRevokeAsync(DeviceConnection connection, CancellationToken ct = default)
    {
        var provider = _providers.Value.ConfigFor(connection.DeviceType);

        if (string.IsNullOrWhiteSpace(provider?.RevocationUrl))
        {
            // Information, not Warning: every local machine and every environment before the
            // provider block is filled in lands here, and a log line that fires on every
            // disconnect in development stops being read by the time it matters.
            _logger.LogInformation(
                "No revocation endpoint is configured for {DeviceType}; DeviceConnection "
                + "{DeviceConnectionId} was disconnected without telling the provider.",
                connection.DeviceType, connection.Id);
            return false;
        }

        // The refresh token first: revoking it ends the grant, not just one access token.
        var stored = connection.RefreshToken ?? connection.AccessToken;
        if (stored is null)
        {
            _logger.LogInformation(
                "DeviceConnection {DeviceConnectionId} has no stored token to revoke.",
                connection.Id);
            return false;
        }

        string token;
        try
        {
            token = _encryption.Decrypt(stored);
        }
        catch (Exception ex)
        {
            // A token we cannot decrypt is one we cannot revoke, and it is worth knowing about:
            // it means a live grant at the provider that this system can no longer end by itself.
            _logger.LogWarning(ex,
                "DeviceConnection {DeviceConnectionId} has a token that could not be decrypted, "
                + "so its grant could not be revoked. It has to be revoked from the wearer's "
                + "Google account instead.",
                connection.Id);
            return false;
        }

        using var client = _httpClientFactory.CreateClient();
        var body = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token });

        try
        {
            var response = await client.PostAsync(provider.RevocationUrl, body, ct);

            if (response.IsSuccessStatusCode || IsAlreadyInvalid(response.StatusCode))
                return true;

            // Status only — a revocation error body can echo the token back.
            _logger.LogWarning(
                "Revoking the grant for DeviceConnection {DeviceConnectionId} returned "
                + "{StatusCode}. The grant may still be live at the provider, and nothing will "
                + "retry it once the connection row is gone.",
                connection.Id, (int)response.StatusCode);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "The call to revoke the grant for DeviceConnection {DeviceConnectionId} failed. "
                + "The grant may still be live at the provider.",
                connection.Id);
            return false;
        }
    }

    /// <summary>
    /// A token the provider will not accept is a token that cannot be used, which is what
    /// revocation was for. Google answers <c>400 invalid_token</c> rather than the 200 RFC 7009
    /// §2.2 suggests.
    /// </summary>
    private static bool IsAlreadyInvalid(System.Net.HttpStatusCode status) =>
        status is System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.Unauthorized;
}
