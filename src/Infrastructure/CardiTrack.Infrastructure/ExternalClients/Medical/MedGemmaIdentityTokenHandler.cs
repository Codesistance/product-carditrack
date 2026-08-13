using System.Net.Http.Headers;
using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.Logging;

namespace CardiTrack.Infrastructure.ExternalClients.Medical;

/// <summary>
/// Attaches a Google-minted OIDC identity token to every MedGemma request so Cloud Run's IAM check
/// (<c>roles/run.invoker</c>) admits it.
/// </summary>
/// <remarks>
/// <para>
/// This lives in a <see cref="DelegatingHandler"/> rather than in <see cref="MedGemmaClient"/> on
/// purpose. That class carries the DPIA privacy invariant over prompts and completions, and its
/// send path is a caller-supplied delegate; threading credentials through it would put auth
/// concerns inside the one class that must stay focused on never leaking health data. Here the
/// header is set once, on the way out, for every operation the client will ever add.
/// </para>
/// <para>
/// <b>The token is a bearer credential and must never be logged.</b> Nothing in this class writes
/// the header, the token, or any part of either to a log, span or exception message — the only
/// signals emitted are the audience (a public service hostname) and failure categories. A 403 is
/// diagnosed from the Cloud Run side, never by echoing the token.
/// </para>
/// </remarks>
internal sealed class MedGemmaIdentityTokenHandler : DelegatingHandler
{
    private readonly string _audience;
    private readonly ILogger<MedGemmaIdentityTokenHandler> _logger;
    private readonly SemaphoreSlim _initGate = new(1, 1);

    /// <summary>
    /// Cached provider, not a cached token string. <see cref="OidcToken"/> refreshes itself as the
    /// token nears expiry, so hand-rolled lifetime tracking here would only be a second, worse
    /// implementation of what it already does.
    /// </summary>
    private OidcToken? _oidcToken;

    public MedGemmaIdentityTokenHandler(string baseUrl, ILogger<MedGemmaIdentityTokenHandler> logger)
    {
        // Cloud Run matches the token's `aud` against the service URL exactly; a trailing slash is a
        // mismatch and yields a 403 that looks like a missing IAM binding.
        _audience = baseUrl.TrimEnd('/');
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
                    // Debug, not Information: IHttpClientFactory rotates pooled handlers on its
                    // default lifetime, so a new instance mints a fresh provider periodically for
                    // the life of the process. At Information this would be recurring noise in
                    // Cloud Logging for a line that only matters when diagnosing a 403.
                    _logger.LogDebug(
                        "MedGemma identity tokens will be minted for audience {Audience}.", _audience);
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
