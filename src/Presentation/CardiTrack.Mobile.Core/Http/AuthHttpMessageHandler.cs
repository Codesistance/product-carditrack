using System.Net;
using System.Net.Http.Headers;
using CardiTrack.Mobile.Core.Auth;

namespace CardiTrack.Mobile.Core.Http;

/// <summary>
/// Attaches the bearer token to API requests and retries exactly once after a 401 by
/// forcing a refresh. Requests must use buffered content (JsonContent/StringContent)
/// so the retry can re-send the body.
/// </summary>
public sealed class AuthHttpMessageHandler : DelegatingHandler
{
    private readonly ITokenRefresher _refresher;

    public AuthHttpMessageHandler(ITokenRefresher refresher)
    {
        _refresher = refresher;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var token = await _refresher.GetValidAccessTokenAsync(forceRefresh: false, ct);
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await base.SendAsync(request, ct);
        if (response.StatusCode != HttpStatusCode.Unauthorized || token is null)
            return response;

        var refreshed = await _refresher.GetValidAccessTokenAsync(forceRefresh: true, ct);
        if (refreshed is null)
            return response;

        response.Dispose();
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", refreshed);
        return await base.SendAsync(request, ct);
    }
}
