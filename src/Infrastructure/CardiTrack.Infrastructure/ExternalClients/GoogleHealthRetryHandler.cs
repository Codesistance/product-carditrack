using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;

namespace CardiTrack.Infrastructure.ExternalClients;

/// <summary>
/// Retries a Google Health API response that says "not now" rather than "no". Every request this
/// client makes is a read — the one POST, <c>dataPoints:dailyRollUp</c>, is a query expressed as a
/// POST because its range does not fit in a URL — so re-sending one cannot double a write.
/// </summary>
/// <remarks>
/// <para>
/// Without this, a single 500 from Google failed a whole member sync: the client throws
/// <see cref="GoogleHealthApiException"/> on any non-success status, and the sync services treat
/// that as the member's turn being over. Google's own 500s are frequently a
/// <c>DEADLINE_EXCEEDED</c> on an internal Fitbit RPC — a 10-second deadline expiring inside their
/// estate, reported to us as INTERNAL — which is precisely the shape of failure that succeeds when
/// asked again a moment later.
/// </para>
/// <para>
/// A handler rather than a retry at each of the client's eight send sites: the policy is one
/// decision about one dependency, and putting it in the pipeline keeps it that way. It sits
/// outside the client's own error handling, so 400 and 404 continue to mean what
/// <see cref="GoogleHealthApiClient"/> already decided they mean (an absent data type, an
/// unpaired account) and are never retried.
/// </para>
/// <para>
/// Logging follows the MedGemma client's rule: nothing per attempt, since a sustained outage
/// already produces one error per call at the caller. A retry that rescued the call logs one
/// warning, because "it took N attempts" is worth knowing.
/// </para>
/// </remarks>
internal sealed class GoogleHealthRetryHandler : DelegatingHandler
{
    /// <summary>Attempts per request, including the first.</summary>
    private const int MaxAttempts = 3;

    private static readonly TimeSpan BackoffStep = TimeSpan.FromSeconds(1);

    /// <summary>Ceiling on a server-advised <c>Retry-After</c>, so a long one cannot park a sync.</summary>
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private readonly ILogger<GoogleHealthRetryHandler> _logger;
    private readonly TimeProvider _timeProvider;

    public GoogleHealthRetryHandler(ILogger<GoogleHealthRetryHandler> logger, TimeProvider? timeProvider = null)
    {
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                if (attempt > 1)
                {
                    _logger.LogWarning(
                        "Google Health {Method} {Path} succeeded on attempt {Attempt} of {MaxAttempts} "
                        + "after a transient failure.",
                        request.Method, request.RequestUri?.AbsolutePath, attempt, MaxAttempts);
                }
                return response;
            }

            if (attempt >= MaxAttempts || !IsTransient(response.StatusCode))
                return response;

            var delay = BackoffFor(response.Headers.RetryAfter, attempt);

            // The failed response is never handed back, so it is ours to dispose — leaving it to
            // the finalizer would hold a pooled connection open for the whole retry.
            response.Dispose();
            await Task.Delay(delay, _timeProvider, cancellationToken);
        }
    }

    /// <summary>
    /// Statuses worth asking again. Deliberately narrow: 5xx and 429 are the far side's condition,
    /// not the request's, and 408 is the far side saying it gave up waiting. Everything else —
    /// 400, 401, 403, 404 — will answer the same way however many times it is asked, and the
    /// client already reads meaning into several of them.
    /// </summary>
    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
        || (int)statusCode >= 500;

    /// <summary>
    /// A server that said <c>Retry-After</c> is answered on its own terms, capped by
    /// <see cref="MaxBackoff"/>; otherwise a linear 1s, 2s. Short on purpose — a member sync runs
    /// dozens of these reads in sequence, so a wait long enough to matter to Google's queue would
    /// be long enough to push the sync past its own schedule.
    /// </summary>
    private TimeSpan BackoffFor(RetryConditionHeaderValue? retryAfter, int attempt)
    {
        if (AdvisedDelay(retryAfter) is { } advised)
            return advised > MaxBackoff ? MaxBackoff : advised;

        return BackoffStep * attempt;
    }

    /// <summary>
    /// The <c>Retry-After</c> value as a delay, in either of the forms RFC 9110 allows: a delta in
    /// seconds, or an HTTP date. A date already in the past yields <see cref="TimeSpan.Zero"/>
    /// rather than a negative wait.
    /// </summary>
    private TimeSpan? AdvisedDelay(RetryConditionHeaderValue? retryAfter)
    {
        if (retryAfter is null)
            return null;

        if (retryAfter.Delta is { } delta)
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;

        if (retryAfter.Date is { } date)
        {
            var until = date - _timeProvider.GetUtcNow();
            return until < TimeSpan.Zero ? TimeSpan.Zero : until;
        }

        return null;
    }
}
