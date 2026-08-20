namespace CardiTrack.Mobile.Core.Api;

/// <summary>
/// Applies a per-request timeout, so one endpoint's legitimately slow answer doesn't force the
/// whole client to wait that long everywhere. <see cref="HttpClient.Timeout"/> is a single
/// client-wide value and a hard ceiling — a request can only be given <em>less</em> time than
/// it, never more — so the client's own timeout is raised to the slowest call this app makes
/// (the member-chat send, see <c>CardiTrackApiClient.MemberChatSendTimeout</c>) and this
/// handler brings every request that didn't ask for more back down to the old default.
/// </summary>
/// <remarks>
/// On expiry this throws <see cref="TimeoutException"/> rather than letting the linked
/// cancellation surface as <see cref="TaskCanceledException"/> — the same distinction
/// <c>MedGemmaClient.SendOnceAsync</c> draws server-side: "the caller gave up" and "the far
/// side was too slow" need different user-facing messages, and the exception type is how
/// <c>CardiTrackApiClient.NetworkError</c> tells them apart. A caller's own cancellation is
/// re-thrown untouched.
/// </remarks>
public sealed class TimeoutHandler : DelegatingHandler
{
    /// <summary>Set on a request to give it more (or less) time than the client default.</summary>
    public static readonly HttpRequestOptionsKey<TimeSpan> TimeoutOption = new("CardiTrack.RequestTimeout");

    private readonly TimeSpan _defaultTimeout;

    public TimeoutHandler(TimeSpan defaultTimeout) => _defaultTimeout = defaultTimeout;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var timeout = request.Options.TryGetValue(TimeoutOption, out var perRequest)
            ? perRequest
            : _defaultTimeout;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            return await base.SendAsync(request, cts.Token);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"The request did not complete within {timeout.TotalSeconds:0} s.", ex);
        }
    }
}
