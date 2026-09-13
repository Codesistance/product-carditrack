using System.Net;
using System.Text;
using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Core.Auth;
using CardiTrack.Mobile.Core.Offline;
using NSubstitute;

namespace CardiTrack.UnitTests.Mobile;

public class OfflineCacheWarmerTests
{
    private static readonly Guid MemberId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid AlertId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    public async Task DoesNothing_WhenThereIsNoSession()
    {
        var tokens = Substitute.For<ITokenStore>();
        tokens.GetAsync().Returns((AuthTokens?)null);
        var (warmer, http, _) = CreateSut(tokens);

        await warmer.RefreshAsync();

        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task PullsAccountAndMemberReads_AndWritesThemToTheCache()
    {
        var (warmer, http, cache) = CreateSut(SignedIn());
        Script(http);

        await warmer.RefreshAsync();

        var paths = http.Requests.Select(r => r.Uri!.PathAndQuery).ToList();
        Assert.Contains("/api/Onboarding/cardimembers", paths);
        Assert.Contains("/api/v1/notifications/summary", paths);
        Assert.Contains("/api/v1/alerts?status=open", paths);
        Assert.Contains($"/api/v1/cardimembers/{MemberId}/alerts?status=open", paths);
        Assert.Contains($"/api/v1/cardimembers/{MemberId}/dashboard", paths);
        Assert.Contains($"/api/v1/insights/members/{MemberId}/digest", paths);
        Assert.Contains($"/api/v1/alerts/{AlertId}", paths);
        Assert.Contains(paths, p => p.Contains("audience=daybook"));
        Assert.Contains(paths, p => p.Contains("audience=weekbook"));
        Assert.Contains(paths, p => p.Contains("audience=monthbook"));

        Assert.True(cache.Items.ContainsKey("api/Onboarding/cardimembers"));
        Assert.True(cache.Items.ContainsKey($"api/v1/cardimembers/{MemberId}/dashboard"));
    }

    [Fact]
    public async Task ConcurrentCalls_ShareOneRun()
    {
        var handler = new HoldingHandler();
        var tokens = SignedIn();
        var api = new CardiTrackApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.test") },
            new MemoryOfflineCache());
        var warmer = new OfflineCacheWarmer(api, tokens);

        var first = warmer.RefreshAsync();
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = warmer.RefreshAsync();

        Assert.Same(first, second);
        handler.Release.SetResult();
        await first;

        Assert.Equal(1, handler.MembersRequests);
    }

    [Fact]
    public async Task DrainForSignOut_CancelsTheSharedRun_AndBlocksANewOneUntilResume()
    {
        var handler = new HoldingHandler();
        var tokens = SignedIn();
        var cache = new MemoryOfflineCache();
        var api = new CardiTrackApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.test") }, cache);
        var warmer = new OfflineCacheWarmer(api, tokens);

        var warm = warmer.RefreshAsync();
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await warmer.DrainForSignOutAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await warm;

        Assert.DoesNotContain($"api/v1/cardimembers/{MemberId}/dashboard", cache.Items.Keys);
        Assert.Equal(1, handler.MembersRequests);

        await warmer.RefreshAsync();
        Assert.Equal(1, handler.MembersRequests);

        warmer.ResumeAfterSignOut();
        handler.Release.SetResult();
        await warmer.RefreshAsync();

        Assert.Equal(2, handler.MembersRequests);
    }

    [Fact]
    public async Task DrainForSignOut_WaitsForTheSharedRun_EvenWhenTheCallerTokenIsCanceled()
    {
        var handler = new UncancellableHoldHandler();
        var tokens = SignedIn();
        var api = new CardiTrackApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.test") },
            new MemoryOfflineCache());
        var warmer = new OfflineCacheWarmer(api, tokens);

        var warm = warmer.RefreshAsync();
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var drain = warmer.DrainForSignOutAsync(canceled.Token);

        Assert.False(drain.IsCompleted);
        handler.Release.SetResult();
        await drain.WaitAsync(TimeSpan.FromSeconds(2));
        await warm;
    }

    [Fact]
    public async Task OneFailedRead_DoesNotStopTheOthers()
    {
        var (warmer, http, cache) = CreateSut(SignedIn());
        http.Enqueue(HttpStatusCode.OK, MembersEnvelope());
        http.Enqueue(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/digest", StringComparison.Ordinal))
                return Json("""{"success":false,"message":"none","timestamp":"2026-08-01T00:00:00Z"}""", HttpStatusCode.NotFound);
            return Json(EnvelopeFor(req.RequestUri));
        });

        await warmer.RefreshAsync();

        Assert.True(cache.Items.ContainsKey($"api/v1/cardimembers/{MemberId}/dashboard"));
        Assert.False(cache.Items.ContainsKey($"api/v1/insights/members/{MemberId}/digest"));
    }

    private static ITokenStore SignedIn()
    {
        var tokens = Substitute.For<ITokenStore>();
        tokens.GetAsync().Returns(new AuthTokens("access", "refresh", "id", DateTimeOffset.UtcNow.AddHours(1)));
        return tokens;
    }

    private static (OfflineCacheWarmer Warmer, FakeHttpMessageHandler Http, MemoryOfflineCache Cache) CreateSut(
        ITokenStore tokens)
    {
        var http = new FakeHttpMessageHandler();
        var cache = new MemoryOfflineCache();
        var api = new CardiTrackApiClient(
            new HttpClient(http) { BaseAddress = new Uri("https://api.test") }, cache);
        return (new OfflineCacheWarmer(api, tokens), http, cache);
    }

    private static void Script(FakeHttpMessageHandler http) =>
        http.Enqueue(req => Json(EnvelopeFor(req.RequestUri!)));

    private static string EnvelopeFor(Uri uri)
    {
        var path = uri.PathAndQuery;
        if (path == "/api/Onboarding/cardimembers")
            return MembersEnvelope();
        if (path == "/api/Onboarding/status")
            return Envelope("""{"hasOrganization":true,"hasUserAccount":true,"hasCardiMember":true,"currentStep":7,"totalSteps":7}""");
        if (path is "/api/v1/alerts" || path.Contains("/alerts?", StringComparison.Ordinal)
            || path.EndsWith("/alerts", StringComparison.Ordinal))
            return Envelope(AlertListEnvelope());
        if (path == $"/api/v1/alerts/{AlertId}")
            return Envelope($$"""{"alertId":"{{AlertId}}","cardiMemberId":"{{MemberId}}","type":"Inactivity","severity":"red","status":"new","title":"t","message":"m","triggeredAt":"2026-08-01T00:00:00Z","aboutDate":"2026-08-01"}""");
        if (path.Contains("/digests", StringComparison.Ordinal)
            || path.Contains("/mutes", StringComparison.Ordinal)
            || path.Contains("/consents", StringComparison.Ordinal)
            || path.Contains("/alarms", StringComparison.Ordinal))
            return Envelope("[]");
        if (path.EndsWith("/sessions", StringComparison.Ordinal))
            return Envelope("""{"sessions":[]}""");
        if (path.EndsWith("/sessions/current", StringComparison.Ordinal))
            return """{"success":true,"message":"ok","data":null,"timestamp":"2026-08-01T00:00:00Z"}""";
        return Envelope("{}");
    }

    private static string MembersEnvelope() =>
        Envelope($$"""[{"id":"{{MemberId}}","name":"Dad","isActive":true}]""");

    private static string AlertListEnvelope() =>
        $$"""{"alerts":[{"alertId":"{{AlertId}}","cardiMemberId":"{{MemberId}}","type":"Inactivity","severity":"red","status":"new","title":"t","message":"m","triggeredAt":"2026-08-01T00:00:00Z","aboutDate":"2026-08-01"}],"total":1,"unreadCount":1}""";

    private static string Envelope(string data) =>
        $$"""{"success":true,"message":"ok","data":{{data}},"timestamp":"2026-08-01T00:00:00Z"}""";

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>
    /// Holds the members GET on an awaited task — never <c>GetResult</c> on the HTTP
    /// thread, which is how the previous version deadlocked the runner.
    /// </summary>
    private sealed class HoldingHandler : HttpMessageHandler
    {
        public TaskCompletionSource Entered { get; } = new();
        public TaskCompletionSource Release { get; } = new();
        public int MembersRequests;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath == "/api/Onboarding/cardimembers")
            {
                Interlocked.Increment(ref MembersRequests);
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }

            return Json(EnvelopeFor(request.RequestUri!));
        }
    }

    /// <summary>
    /// Same as <see cref="HoldingHandler"/> but the members GET ignores cancel, so a drain
    /// that used the caller's token would return while this request was still in flight.
    /// </summary>
    private sealed class UncancellableHoldHandler : HttpMessageHandler
    {
        public TaskCompletionSource Entered { get; } = new();
        public TaskCompletionSource Release { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath == "/api/Onboarding/cardimembers")
            {
                Entered.TrySetResult();
                await Release.Task;
            }

            return Json(EnvelopeFor(request.RequestUri!));
        }
    }
}
