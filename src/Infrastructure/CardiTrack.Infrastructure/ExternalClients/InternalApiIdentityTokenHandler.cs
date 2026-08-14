using System.Net.Http.Headers;
using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.Logging;

namespace CardiTrack.Infrastructure.ExternalClients;

/// <summary>
/// Attaches a Google-minted OIDC identity token so the API's GoogleOidc scheme
/// (notification_engine.md §7.2 C4) admits the pipeline as the pinned service account.
/// </summary>
/// <remarks>
/// The token is a bearer credential and must never be logged. The only signals emitted are the
/// audience (a public identifier, not a hostname on this path) and failure categories.
/// </remarks>
internal sealed class InternalApiIdentityTokenHandler : DelegatingHandler
{
    private readonly string _audience;
    private readonly ILogger<InternalApiIdentityTokenHandler> _logger;
    private readonly SemaphoreSlim _initGate = new(1, 1);

    /// <summary>
    /// Cached provider, not a cached token string. <see cref="OidcToken"/> refreshes itself as the
    /// token nears expiry.
    /// </summary>
    private OidcToken? _oidcToken;

    public InternalApiIdentityTokenHandler(string audience, ILogger<InternalApiIdentityTokenHandler> logger)
    {
        _audience = audience;
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var token = await GetTokenAsync(cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await base.SendAsync(request, cancellationToken);
    }

    private async Task<string> GetTokenAsync(CancellationToken ct)
    {
        if (_oidcToken is null)
        {
            await _initGate.WaitAsync(ct);
            try
            {
                if (_oidcToken is null)
                {
                    var credential = await GoogleCredential.GetApplicationDefaultAsync(ct);
                    _oidcToken = await credential.GetOidcTokenAsync(
                        OidcTokenOptions.FromTargetAudience(_audience), ct);
                    _logger.LogDebug(
                        "Internal API identity tokens will be minted for audience {Audience}.", _audience);
                }
            }
            finally
            {
                _initGate.Release();
            }
        }

        return await _oidcToken.GetAccessTokenAsync(ct)
            ?? throw new InvalidOperationException(
                $"Application Default Credentials returned no identity token for audience '{_audience}'. "
                + "On Cloud Run this means the metadata server is unreachable or the runtime service "
                + "account cannot mint tokens.");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _initGate.Dispose();
        }

        base.Dispose(disposing);
    }
}
