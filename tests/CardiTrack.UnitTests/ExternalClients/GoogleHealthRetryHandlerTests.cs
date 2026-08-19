using System.Net;
using System.Net.Http.Headers;
using CardiTrack.Infrastructure.ExternalClients;
using CardiTrack.UnitTests.Mobile;
using Microsoft.Extensions.Logging;

namespace CardiTrack.UnitTests.ExternalClients;

/// <summary>
/// Pins which Google Health failures are worth asking about again. The distinction is the whole
/// value of the handler: a 500 that is Google's internal DEADLINE_EXCEEDED costs one member their
/// whole sync if it is taken as final, and a 404 that means "this account has no such data type"
/// costs the sync three times as long if it is not.
/// </summary>
public class GoogleHealthRetryHandlerTests
{
    private const string Payload = """{"rollupDataPoints":[]}""";

    [Fact]
    public async Task RetriesAServerError_AndReturnsTheEventualSuccess()
    {
        var inner = new FakeHttpMessageHandler()
            .Enqueue(HttpStatusCode.InternalServerError, """{"error":{"code":500,"status":"INTERNAL"}}""")
            .Enqueue(HttpStatusCode.OK, Payload);
        var client = CreateClient(inner, out var logger, out _);

        var response = await client.GetAsync("/v4/users/me/dataTypes/steps/dataPoints");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, inner.Requests.Count);
        // One note that it took a retry, not one line per attempt — the MedGemma client's rule.
        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("2", warning.Message);
    }

    /// <summary>
    /// The shape the dev fleet actually produced: an internal Fitbit RPC whose 10-second deadline
    /// expired, surfaced to callers as a plain 500 INTERNAL.
    /// </summary>
    [Fact]
    public async Task RetriesADeadlineExceededReportedAsInternal()
    {
        var inner = new FakeHttpMessageHandler()
            .Enqueue(
                HttpStatusCode.InternalServerError,
                """{"error":{"code":500,"message":"...FitbitTimeSeriesService.QuerySeries, DEADLINE_EXCEEDED...","status":"INTERNAL"}}""")
            .Enqueue(HttpStatusCode.ServiceUnavailable, """{"error":{"code":503,"status":"UNAVAILABLE"}}""")
            .Enqueue(HttpStatusCode.OK, Payload);
        var client = CreateClient(inner, out _, out _);

        var response = await client.GetAsync("/v4/users/me/dataTypes/heart-rate/dataPoints");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, inner.Requests.Count);
    }

    [Fact]
    public async Task GivesUpAfterThreeAttempts_AndHandsBackTheFailingResponse()
    {
        var inner = new FakeHttpMessageHandler()
            .Enqueue(HttpStatusCode.InternalServerError, """{"error":{"code":500}}""");
        var client = CreateClient(inner, out var logger, out _);

        var response = await client.GetAsync("/v4/users/me/dataTypes/steps/dataPoints");

        // Handed back rather than thrown: GoogleHealthApiClient owns what a status means, and its
        // exception carries the body this handler must not consume.
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("500", await response.Content.ReadAsStringAsync());
        Assert.Equal(3, inner.Requests.Count);
        // A sustained outage already logs one error per call at the caller; the handler adds none.
        Assert.Empty(logger.Entries);
    }

    /// <summary>
    /// 400 and 404 are answers, not blips — the client reads an absent data type and an unpaired
    /// account out of them. Retrying would triple the length of a sync that is working correctly.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task DoesNotRetryAClientError(HttpStatusCode status)
    {
        var inner = new FakeHttpMessageHandler().Enqueue(status, """{"error":{}}""");
        var client = CreateClient(inner, out _, out _);

        var response = await client.GetAsync("/v4/users/me/dataTypes/daily-vo2-max/dataPoints");

        Assert.Equal(status, response.StatusCode);
        Assert.Single(inner.Requests);
    }

    [Fact]
    public async Task DoesNotRetryASuccess()
    {
        var inner = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, Payload);
        var client = CreateClient(inner, out var logger, out _);

        await client.GetAsync("/v4/users/me/identity");

        Assert.Single(inner.Requests);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task BacksOffLinearly_WhenTheServerAdvisesNothing()
    {
        var inner = new FakeHttpMessageHandler()
            .Enqueue(HttpStatusCode.BadGateway, "")
            .Enqueue(HttpStatusCode.BadGateway, "")
            .Enqueue(HttpStatusCode.OK, Payload);
        var client = CreateClient(inner, out _, out var time);

        await client.GetAsync("/v4/users/me/dataTypes/steps/dataPoints");

        Assert.Equal(new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2) }, time.Delays);
    }

    [Fact]
    public async Task WaitsWhatTheServerAsksFor_WhenItSendsRetryAfter()
    {
        var inner = new FakeHttpMessageHandler()
            .Enqueue(RetryAfter(HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(4)))
            .Enqueue(HttpStatusCode.OK, Payload);
        var client = CreateClient(inner, out _, out var time);

        await client.GetAsync("/v4/users/me/dataTypes/steps/dataPoints");

        Assert.Equal(new[] { TimeSpan.FromSeconds(4) }, time.Delays);
    }

    /// <summary>A member sync runs dozens of these reads in sequence; none may park it for an hour.</summary>
    [Fact]
    public async Task CapsARetryAfterThatWouldParkTheSync()
    {
        var inner = new FakeHttpMessageHandler()
            .Enqueue(RetryAfter(HttpStatusCode.ServiceUnavailable, TimeSpan.FromHours(1)))
            .Enqueue(HttpStatusCode.OK, Payload);
        var client = CreateClient(inner, out _, out var time);

        await client.GetAsync("/v4/users/me/dataTypes/steps/dataPoints");

        Assert.Equal(new[] { TimeSpan.FromSeconds(30) }, time.Delays);
    }

    /// <summary>
    /// The one POST this client makes is <c>dataPoints:dailyRollUp</c> — a query whose range does
    /// not fit in a URL, not a write — so its body has to survive being sent again.
    /// </summary>
    [Fact]
    public async Task ResendsThePostBody_OnARetriedRollupQuery()
    {
        var inner = new FakeHttpMessageHandler()
            .Enqueue(HttpStatusCode.InternalServerError, "")
            .Enqueue(HttpStatusCode.OK, Payload);
        var client = CreateClient(inner, out _, out _);
        const string body = """{"range":{"start":"2026-08-19T00:00:00","end":"2026-08-20T00:00:00"},"windowSizeDays":1}""";

        await client.PostAsync(
            "/v4/users/me/dataTypes/steps/dataPoints:dailyRollUp",
            new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(2, inner.Requests.Count);
        Assert.All(inner.Requests, r => Assert.Equal(body, r.Body));
    }

    private static HttpClient CreateClient(
        FakeHttpMessageHandler inner, out ListLogger logger, out InstantRetryTimeProvider time)
    {
        logger = new ListLogger();
        time = new InstantRetryTimeProvider();
        var handler = new GoogleHealthRetryHandler(logger, time) { InnerHandler = inner };
        return new HttpClient(handler) { BaseAddress = new Uri("https://health.googleapis.com") };
    }

    private static Func<HttpRequestMessage, HttpResponseMessage> RetryAfter(
        HttpStatusCode status, TimeSpan delta) =>
        _ =>
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent("") };
            response.Headers.RetryAfter = new RetryConditionHeaderValue(delta);
            return response;
        };

    /// <summary>Records the backoff and then resolves it immediately, so the suite stays fast.</summary>
    private sealed class InstantRetryTimeProvider : TimeProvider
    {
        public List<TimeSpan> Delays { get; } = new();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (Delays) Delays.Add(dueTime);
            return base.CreateTimer(callback, state, TimeSpan.Zero, period);
        }
    }

    /// <summary>Hand-rolled recording logger, matching the suite's no-mocking-library style.</summary>
    private sealed class ListLogger : ILogger<GoogleHealthRetryHandler>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Entries) Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
