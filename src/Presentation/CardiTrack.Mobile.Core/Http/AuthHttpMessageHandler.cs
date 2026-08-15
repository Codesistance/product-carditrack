using System.Net;
using System.Net.Http.Headers;
using CardiTrack.Mobile.Core.Auth;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CardiTrack.Mobile.Core.Http;

/// <summary>
/// Attaches the bearer token to API requests and retries exactly once after a 401 by
/// forcing a refresh. Requests must use buffered content (JsonContent/StringContent)
/// so the retry can re-send the body.
/// </summary>
public sealed class AuthHttpMessageHandler : DelegatingHandler
{
    private readonly ITokenRefresher _refresher;
    private readonly ILogger<AuthHttpMessageHandler> _logger;

    public AuthHttpMessageHandler(ITokenRefresher refresher, ILogger<AuthHttpMessageHandler>? logger = null)
    {
        _refresher = refresher;
        _logger = logger ?? NullLogger<AuthHttpMessageHandler>.Instance;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var token = await _refresher.GetValidAccessTokenAsync(forceRefresh: false, ct);
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await base.SendAsync(request, ct);
        if (response.StatusCode != HttpStatusCode.Unauthorized || token is null)
            return response;

        _logger.LogInformation("API returned 401 for {Path}; forcing a token refresh and retrying once",
            request.RequestUri?.AbsolutePath);

        var refreshed = await _refresher.GetValidAccessTokenAsync(forceRefresh: true, ct);
        if (refreshed is null || refreshed == token)
        {
            _logger.LogWarning("Token refresh after 401 did not produce a new token for {Path}",
                request.RequestUri?.AbsolutePath);
            return response;
        }

        response.Dispose();
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", refreshed);
        return await base.SendAsync(request, ct);
    }
}
