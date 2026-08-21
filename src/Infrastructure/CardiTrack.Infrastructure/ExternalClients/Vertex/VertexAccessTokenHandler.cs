using System.Net.Http.Headers;
using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.Logging;

namespace CardiTrack.Infrastructure.ExternalClients.Vertex;

/// <summary>
/// Attaches a Google OAuth2 access token (scope <c>cloud-platform</c>) to every Vertex AI request.
/// The Vertex endpoint authorises by IAM (<c>roles/aiplatform.user</c> on the caller's service
/// account), not by API key — the sibling of <see cref="Medical.MedGemmaIdentityTokenHandler"/>,
/// which mints audience-bound OIDC identity tokens for Cloud Run instead.
/// </summary>
/// <remarks>
/// <para>
/// Auth lives in a <see cref="DelegatingHandler"/> rather than in <see cref="VertexAiClient"/> for
/// the same reason as the identity-token handler: that class carries the DPIA privacy invariant
/// over prompts and completions, and threading credentials through it would put auth concerns
/// inside the one class that must stay focused on never leaking health data.
/// </para>
/// <para>
/// <b>The token is a bearer credential and must never be logged.</b> Nothing in this class writes
/// the header, the token, or any part of either to a log, span or exception message. A 403 is
/// diagnosed from the IAM side, never by echoing the token.
/// </para>
/// </remarks>
internal sealed class VertexAccessTokenHandler : DelegatingHandler
{
    private const string CloudPlatformScope = "https://www.googleapis.com/auth/cloud-platform";

    private readonly ILogger<VertexAccessTokenHandler> _logger;
    private readonly SemaphoreSlim _initGate = new(1, 1);

    /// <summary>
    /// Cached credential, not a cached token string. <see cref="ITokenAccess"/> implementations
    /// refresh themselves as the token nears expiry, so hand-rolled lifetime tracking here would
    /// only be a second, worse implementation of what they already do.
    /// </summary>
    private ITokenAccess? _credential;

    public VertexAccessTokenHandler(ILogger<VertexAccessTokenHandler> logger)
    {
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
        if (_credential is null)
        {
            await _initGate.WaitAsync(ct);
            try
            {
                if (_credential is null)
                {
                    var credential = await GoogleCredential.GetApplicationDefaultAsync(ct);
                    _credential = credential.CreateScoped(CloudPlatformScope);
                    // Debug, not Information: IHttpClientFactory rotates pooled handlers on its
                    // default lifetime, so a new instance initialises a fresh credential
                    // periodically for the life of the process. At Information this would be
                    // recurring noise in Cloud Logging for a line that only matters when
                    // diagnosing a 403.
                    _logger.LogDebug("Vertex AI access tokens will be minted via Application Default Credentials.");
                }
            }
            finally
            {
                _initGate.Release();
            }
        }

        return await _credential.GetAccessTokenForRequestAsync(cancellationToken: ct)
            ?? throw new InvalidOperationException(
                "Application Default Credentials returned no access token for Vertex AI. "
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
