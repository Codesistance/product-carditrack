using CardiTrack.Infrastructure.Settings;

namespace CardiTrack.Infrastructure.ExternalClients;

/// <summary>Result of an authorization_code token exchange with a device provider.</summary>
public record OAuthTokenResult(
    string AccessToken,
    string? RefreshToken,
    int ExpiresInSeconds,
    string? Scope,
    string? ProviderUserId);

public interface IOAuthCodeExchangeService
{
    /// <summary>
    /// Exchanges an authorization code (with its PKCE verifier) for tokens at the provider's
    /// token endpoint. Throws <see cref="OAuthExchangeException"/> when the provider rejects it.
    /// </summary>
    Task<OAuthTokenResult> ExchangeCodeAsync(
        DeviceProviderSettings providerConfig,
        string code,
        string redirectUri,
        string codeVerifier,
        CancellationToken ct = default);
}

public class OAuthExchangeException : Exception
{
    public OAuthExchangeException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}
