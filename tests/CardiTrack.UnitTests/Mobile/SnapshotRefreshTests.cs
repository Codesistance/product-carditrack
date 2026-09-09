using System.Net;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Core.Offline;
using NSubstitute;

namespace CardiTrack.UnitTests.Mobile;

/// <summary>
/// The sequence every read screen runs: saved snapshot first, live answer behind it, and one of
/// seven outcomes the screen's banner and overlay key off. Exercised through the real client over
/// a scripted handler and an in-memory cache, because the outcome depends on what the client
/// says about where each answer came from.
/// </summary>
public class SnapshotRefreshTests
{
    private const string Members = "api/Onboarding/cardimembers";

    private static string MembersEnvelope(string name) =>
        $$"""{"success":true,"message":"ok","data":[{"id":"{{Guid.NewGuid()}}","name":"{{name}}"}],"timestamp":"2026-08-01T00:00:00Z"}""";

    [Fact]
    public async Task RendersTheSnapshot_ThenTheFreshAnswer_AndReportsFreshReplacedSaved()
    {
        var cache = new MemoryOfflineCache();
        var savedAt = DateTimeOffset.UtcNow.AddHours(-1);
        cache.Items[Members] = new OfflineCacheEntry(MembersEnvelope("Saved"), savedAt);
        var (api, http) = CreateSut(cache);
        http.Enqueue(HttpStatusCode.OK, MembersEnvelope("Fresh"));
        var gate = new LoadGate();
        var feedback = Substitute.For<IRefreshFeedback>();
        var rendered = new List<string>();
        var order = new List<string>();
        feedback.When(f => f.SavedShown(Arg.Any<DateTimeOffset?>())).Do(_ => order.Add("saved-shown"));
        feedback.When(f => f.Replacing()).Do(_ => order.Add("replacing"));
        feedback.When(f => f.Completed(Arg.Any<RefreshOutcome>())).Do(_ => order.Add("completed"));

        var outcome = await SnapshotRefresh.RunAsync(
            api, gate, gate.Begin(),
            peek: ct => api.PeekCardiMembersAsync(ct),
            fetch: ct => api.GetCardiMembersAsync(ct),
            render: members => { rendered.Add(members[0].Name); order.Add($"render:{members[0].Name}"); },
            feedback);

        Assert.Equal(RefreshResult.FreshReplacedSaved, outcome.Result);
        Assert.True(outcome.IsFresh);
        Assert.Equal(["Saved", "Fresh"], rendered);
        Assert.Equal(["render:Saved", "saved-shown", "replacing", "render:Fresh", "completed"], order);
        feedback.Received().SavedShown(savedAt);
        feedback.Received().Completed(Arg.Is<RefreshOutcome>(o => o.Result == RefreshResult.FreshReplacedSaved));
    }

    [Fact]
    public async Task SkipsThePeek_WhenTheScreenAlreadyHasData()
    {
        var cache = new MemoryOfflineCache();
        cache.Items[Members] = new OfflineCacheEntry(MembersEnvelope("Saved"), DateTimeOffset.UtcNow);
        var (api, http) = CreateSut(cache);
        http.Enqueue(HttpStatusCode.OK, MembersEnvelope("Fresh"));
        var gate = new LoadGate();
        var feedback = Substitute.For<IRefreshFeedback>();
        var rendered = new List<string>();

        var outcome = await SnapshotRefresh.RunAsync(
            api, gate, gate.Begin(),
            peek: null,
            fetch: ct => api.GetCardiMembersAsync(ct),
            render: members => rendered.Add(members[0].Name),
            feedback);

        Assert.Equal(RefreshResult.FreshNoSnapshot, outcome.Result);
        Assert.Equal(["Fresh"], rendered);
        feedback.DidNotReceive().SavedShown(Arg.Any<DateTimeOffset?>());
        feedback.DidNotReceive().Replacing();
    }

    [Fact]
    public async Task ReportsFreshNoSnapshot_WhenNothingWasSaved()
    {
        var (api, http) = CreateSut(new MemoryOfflineCache());
        http.Enqueue(HttpStatusCode.OK, MembersEnvelope("Fresh"));
        var gate = new LoadGate();
        var feedback = Substitute.For<IRefreshFeedback>();
        var renders = 0;

        var outcome = await SnapshotRefresh.RunAsync(
            api, gate, gate.Begin(),
            peek: ct => api.PeekCardiMembersAsync(ct),
            fetch: ct => api.GetCardiMembersAsync(ct),
            render: _ => renders++,
            feedback);

        Assert.Equal(RefreshResult.FreshNoSnapshot, outcome.Result);
        Assert.Equal(1, renders);
        feedback.DidNotReceive().SavedShown(Arg.Any<DateTimeOffset?>());
        feedback.DidNotReceive().Replacing();
        feedback.Received(1).Completed(Arg.Any<RefreshOutcome>());
    }

    [Fact]
    public async Task ReportsFreshUnchanged_AndDoesNotRerender_WhenSameAsSaysSo()
    {
        var cache = new MemoryOfflineCache();
        cache.Items[Members] = new OfflineCacheEntry(MembersEnvelope("Dad"), DateTimeOffset.UtcNow);
        var (api, http) = CreateSut(cache);
        http.Enqueue(HttpStatusCode.OK, MembersEnvelope("Dad"));
        var gate = new LoadGate();
        var feedback = Substitute.For<IRefreshFeedback>();
        var renders = 0;

        var outcome = await SnapshotRefresh.RunAsync(
            api, gate, gate.Begin(),
            peek: ct => api.PeekCardiMembersAsync(ct),
            fetch: ct => api.GetCardiMembersAsync(ct),
            render: _ => renders++,
            feedback,
            sameAs: (a, b) => a[0].Name == b[0].Name);

        Assert.Equal(RefreshResult.FreshUnchanged, outcome.Result);
        Assert.True(outcome.IsFresh);
        Assert.Equal(1, renders);
        feedback.DidNotReceive().Replacing();
    }

    [Fact]
    public async Task ReportsSavedOnlyOffline_WhenTheLiveCallFellBackToTheCache()
    {
        var cache = new MemoryOfflineCache();
        var savedAt = DateTimeOffset.UtcNow.AddMinutes(-40);
        cache.Items[Members] = new OfflineCacheEntry(MembersEnvelope("Saved"), savedAt);
        var (api, http) = CreateSut(cache);
        http.Throws(new HttpRequestException("offline"));
        var gate = new LoadGate();
        var feedback = Substitute.For<IRefreshFeedback>();
        var renders = 0;

        var outcome = await SnapshotRefresh.RunAsync(
            api, gate, gate.Begin(),
            peek: ct => api.PeekCardiMembersAsync(ct),
            fetch: ct => api.GetCardiMembersAsync(ct),
            render: _ => renders++,
            feedback);

        Assert.Equal(RefreshResult.SavedOnlyOffline, outcome.Result);
        Assert.Equal(savedAt, outcome.SavedAt);
        Assert.True(outcome.IsSavedOnly);
        Assert.Equal(1, renders);
        feedback.DidNotReceive().Replacing();
    }

    [Fact]
    public async Task RendersTheFallback_WhenThereWasNoPeek_AndTheLiveCallFellBack()
    {
        var cache = new MemoryOfflineCache();
        cache.Items[Members] = new OfflineCacheEntry(MembersEnvelope("Saved"), DateTimeOffset.UtcNow);
        var (api, http) = CreateSut(cache);
        http.Throws(new HttpRequestException("offline"));
        var gate = new LoadGate();
        var rendered = new List<string>();

        var outcome = await SnapshotRefresh.RunAsync(
            api, gate, gate.Begin(),
            peek: null,
            fetch: ct => api.GetCardiMembersAsync(ct),
            render: members => rendered.Add(members[0].Name),
            Substitute.For<IRefreshFeedback>());

        Assert.Equal(RefreshResult.SavedOnlyOffline, outcome.Result);
        Assert.Equal(["Saved"], rendered);
    }

    [Fact]
    public async Task ReportsSavedOnlyOffline_WhenTheLiveCallIsANetworkFailure_AndOnlyThePeekAnswered()
    {
        // The peek answers from one store and the live call finds nothing to fall back on: a
        // client whose cache read raced a write, or a stub. The snapshot stays and it is offline.
        var api = Substitute.For<ICardiTrackApiClient>();
        var snapshot = new List<CardiMemberResponse> { new() { Name = "Saved" } };
        api.PeekCardiMembersAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<List<CardiMemberResponse>?>(snapshot));
        api.GetCardiMembersAsync(Arg.Any<CancellationToken>()).Returns<Task<List<CardiMemberResponse>>>(_ =>
            throw new ApiException(HttpStatusCode.ServiceUnavailable, "No connection.", inner: new HttpRequestException()));
        var gate = new LoadGate();
        var renders = 0;

        var outcome = await SnapshotRefresh.RunAsync(
            api, gate, gate.Begin(),
            peek: ct => api.PeekCardiMembersAsync(ct),
            fetch: ct => api.GetCardiMembersAsync(ct),
            render: _ => renders++,
            Substitute.For<IRefreshFeedback>());

        Assert.Equal(RefreshResult.SavedOnlyOffline, outcome.Result);
        Assert.True(outcome.Error!.IsNetworkFailure);
        Assert.Equal(1, renders);
    }

    [Fact]
    public async Task ReportsSavedOnlyHttpError_OnA500_AndKeepsTheSnapshot()
    {
        var cache = new MemoryOfflineCache();
        var savedAt = DateTimeOffset.UtcNow.AddMinutes(-2);
        cache.Items[Members] = new OfflineCacheEntry(MembersEnvelope("Saved"), savedAt);
        var (api, http) = CreateSut(cache);
        http.Enqueue(HttpStatusCode.InternalServerError, """{"success":false,"message":"boom","timestamp":"2026-08-01T00:00:00Z"}""");
        var gate = new LoadGate();
        var feedback = Substitute.For<IRefreshFeedback>();
        var renders = 0;

        var outcome = await SnapshotRefresh.RunAsync(
            api, gate, gate.Begin(),
            peek: ct => api.PeekCardiMembersAsync(ct),
            fetch: ct => api.GetCardiMembersAsync(ct),
            render: _ => renders++,
            feedback);

        Assert.Equal(RefreshResult.SavedOnlyHttpError, outcome.Result);
        Assert.Equal(savedAt, outcome.SavedAt);
        Assert.Equal("boom", outcome.Error!.Message);
        Assert.Equal(1, renders);
        Assert.Single(cache.Items);
    }

    [Fact]
    public async Task ReportsNothingAndFailed_On404_EvenOverASnapshot()
    {
        var cache = new MemoryOfflineCache();
        cache.Items[Members] = new OfflineCacheEntry(MembersEnvelope("Saved"), DateTimeOffset.UtcNow);
        var (api, http) = CreateSut(cache);
        http.Enqueue(HttpStatusCode.NotFound, """{"success":false,"message":"gone","timestamp":"2026-08-01T00:00:00Z"}""");
        var gate = new LoadGate();

        var outcome = await SnapshotRefresh.RunAsync(
            api, gate, gate.Begin(),
            peek: ct => api.PeekCardiMembersAsync(ct),
            fetch: ct => api.GetCardiMembersAsync(ct),
            render: _ => { },
            Substitute.For<IRefreshFeedback>());

        Assert.Equal(RefreshResult.NothingAndFailed, outcome.Result);
        Assert.False(outcome.HasContent);
        Assert.True(outcome.Error!.IsNotFound);
        // The client forgot the snapshot in the same breath.
        Assert.Empty(cache.Items);
    }

    [Fact]
    public async Task ReportsNothingAndFailed_WhenThereIsNoSnapshotAndTheCallFails()
    {
        var (api, http) = CreateSut(new MemoryOfflineCache());
        http.Throws(new HttpRequestException("offline"));
        var gate = new LoadGate();
        var feedback = Substitute.For<IRefreshFeedback>();
        var renders = 0;

        var outcome = await SnapshotRefresh.RunAsync(
            api, gate, gate.Begin(),
            peek: ct => api.PeekCardiMembersAsync(ct),
            fetch: ct => api.GetCardiMembersAsync(ct),
            render: _ => renders++,
            feedback);

        Assert.Equal(RefreshResult.NothingAndFailed, outcome.Result);
        Assert.Equal(0, renders);
        feedback.Received(1).Completed(Arg.Is<RefreshOutcome>(o => o.Result == RefreshResult.NothingAndFailed));
    }

    [Fact]
    public async Task ReportsSuperseded_AndTellsTheScreenNothing_WhenANewerLoadBeginsMidFetch()
    {
        var cache = new MemoryOfflineCache();
        cache.Items[Members] = new OfflineCacheEntry(MembersEnvelope("Saved"), DateTimeOffset.UtcNow);
        var (api, http) = CreateSut(cache);
        var gate = new LoadGate();
        var ticket = gate.Begin();
        http.Enqueue(_ =>
        {
            // A chip tap while the first load is on the wire.
            gate.Begin();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(MembersEnvelope("Fresh"), System.Text.Encoding.UTF8, "application/json"),
            };
        });
        var feedback = Substitute.For<IRefreshFeedback>();
        var rendered = new List<string>();

        var outcome = await SnapshotRefresh.RunAsync(
            api, gate, ticket,
            peek: ct => api.PeekCardiMembersAsync(ct),
            fetch: ct => api.GetCardiMembersAsync(ct),
            render: members => rendered.Add(members[0].Name),
            feedback);

        Assert.Equal(RefreshResult.Superseded, outcome.Result);
        Assert.Equal(["Saved"], rendered);
        feedback.DidNotReceive().Replacing();
        feedback.DidNotReceive().Completed(Arg.Any<RefreshOutcome>());
    }

    [Fact]
    public async Task ReportsSuperseded_WhenANewerLoadBeginsMidPeek()
    {
        var api = Substitute.For<ICardiTrackApiClient>();
        var gate = new LoadGate();
        var ticket = gate.Begin();
        api.PeekCardiMembersAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            gate.Begin();
            return Task.FromResult<List<CardiMemberResponse>?>([new CardiMemberResponse { Name = "Saved" }]);
        });
        var feedback = Substitute.For<IRefreshFeedback>();
        var renders = 0;

        var outcome = await SnapshotRefresh.RunAsync(
            api, gate, ticket,
            peek: ct => api.PeekCardiMembersAsync(ct),
            fetch: ct => api.GetCardiMembersAsync(ct),
            render: _ => renders++,
            feedback);

        Assert.Equal(RefreshResult.Superseded, outcome.Result);
        Assert.Equal(0, renders);
        await api.DidNotReceive().GetCardiMembersAsync(Arg.Any<CancellationToken>());
        feedback.DidNotReceive().SavedShown(Arg.Any<DateTimeOffset?>());
    }

    [Fact]
    public async Task CompositeScope_DatesTheOutcomeByTheOldestCachedCall()
    {
        var cache = new MemoryOfflineCache();
        var older = DateTimeOffset.UtcNow.AddHours(-5);
        var newer = DateTimeOffset.UtcNow.AddMinutes(-1);
        cache.Items[Members] = new OfflineCacheEntry(MembersEnvelope("Saved"), newer);
        cache.Items["api/v1/notifications/summary"] = new OfflineCacheEntry(
            """{"success":true,"message":"ok","data":{},"timestamp":"2026-08-01T00:00:00Z"}""", older);
        var (api, http) = CreateSut(cache);
        http.Throws(new HttpRequestException("offline"));
        var gate = new LoadGate();

        var outcome = await SnapshotRefresh.RunAsync<MembersAndSummary>(
            api, gate, gate.Begin(),
            peek: null,
            fetch: async (ct, scope) =>
            {
                var members = await scope.Track(api.GetCardiMembersAsync(ct));
                var summary = await scope.Track(api.GetNotificationSummaryAsync(ct));
                return new MembersAndSummary(members, summary);
            },
            render: _ => { },
            Substitute.For<IRefreshFeedback>());

        Assert.Equal(RefreshResult.SavedOnlyOffline, outcome.Result);
        Assert.Equal(older, outcome.SavedAt);
    }

    // A composite is a class, not a tuple: the run tells "no snapshot" from "snapshot" by null.
    private sealed record MembersAndSummary(List<CardiMemberResponse> Members, NotificationSummaryResponse Summary);

    [Fact]
    public async Task RenderFaults_Propagate_ToTheScreen()
    {
        var (api, http) = CreateSut(new MemoryOfflineCache());
        http.Enqueue(HttpStatusCode.OK, MembersEnvelope("Fresh"));
        var gate = new LoadGate();
        var feedback = Substitute.For<IRefreshFeedback>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => SnapshotRefresh.RunAsync(
            api, gate, gate.Begin(),
            peek: null,
            fetch: ct => api.GetCardiMembersAsync(ct),
            render: _ => throw new InvalidOperationException("cannot draw"),
            feedback));

        feedback.DidNotReceive().Completed(Arg.Any<RefreshOutcome>());
    }

    private static (CardiTrackApiClient Api, FakeHttpMessageHandler Http) CreateSut(IOfflineReadCache cache)
    {
        var http = new FakeHttpMessageHandler();
        var api = new CardiTrackApiClient(
            new HttpClient(http) { BaseAddress = new Uri("https://api.test") }, cache);
        return (api, http);
    }
}
