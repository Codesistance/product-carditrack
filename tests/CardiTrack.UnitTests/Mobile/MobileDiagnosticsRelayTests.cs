using System.Net;
using System.Text;
using System.Text.Json;
using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Mobile.Core.Diagnostics;
using CardiTrack.Shared.Http;

namespace CardiTrack.UnitTests.Mobile;

/// <summary>
/// The relay exists for one moment: a crash on a phone with no developer attached. So the cases
/// that matter are that a line survives the process (the queue), that it arrives exactly once
/// with the key and the handset description (the send), and that the two configuration
/// refusals stop it retrying forever while a dead network does not.
/// </summary>
public sealed class MobileDiagnosticsRelayTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "carditrack-relay-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeHttpMessageHandler _handler = new();
    private readonly List<string?> _presentedKeys = [];

    private string QueuePath => Path.Combine(_dir, "queue.jsonl");

    private MobileDiagnosticsRelay Create(string? key = "test-relay-key-0123456789") =>
        new(
            new HttpClient(_handler) { BaseAddress = new Uri("https://api.test/") },
            key,
            QueuePath,
            () => new MobileDiagnosticsLogRequest
            {
                Platform = "ios",
                AppVersion = "0.2.304+1512",
                Device = "Apple iPhone17,1",
                Manufacturer = "Apple",
                Model = "iPhone17,1",
                OsVersion = "26.6.1",
                Runtime = ".NET 10.0.1",
                ModuleVersionId = "0123456789abcdef0123456789abcdef",
                InstallId = "abc123",
            });

    private void Respond(HttpStatusCode status = HttpStatusCode.Accepted) =>
        _handler.Enqueue(request =>
        {
            _presentedKeys.Add(
                request.Headers.TryGetValues(MobileDiagnosticsContract.KeyHeader, out var values)
                    ? values.Single()
                    : null);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
        });

    private static MobileDiagnosticsLogEntry Entry(string message = "boom", string level = "Error") => new()
    {
        Timestamp = new DateTimeOffset(2026, 9, 14, 21, 19, 26, TimeSpan.Zero),
        Level = level,
        Message = message,
        Exception = "System.InvalidOperationException: boom\n   at CardiTrack.Mobile.SignInPage.OnSignInClicked()",
        ExceptionType = "System.InvalidOperationException",
        ExceptionMessage = "boom",
        Frames =
        [
            new MobileDiagnosticsStackFrame
            {
                Depth = 0,
                Index = 0,
                Type = "CardiTrack.Mobile.SignInPage",
                Method = "Void OnSignInClicked(System.Object, System.EventArgs)",
                Assembly = "CardiTrack.Mobile",
                IlOffset = 31,
                NativeIp = "0x1033795b8",
            },
        ],
        Source = "CardiTrack.Mobile.Unhandled",
        Screen = "SignInPage",
        NetworkAccess = "Internet",
        UptimeSeconds = 118.2,
        ThreadId = 1,
        IsMainThread = true,
    };

    private MobileDiagnosticsLogRequest Body(int index) =>
        JsonSerializer.Deserialize<MobileDiagnosticsLogRequest>(_handler.Requests[index].Body!, Json)!;

    [Fact]
    public async Task WithoutAKey_NothingIsQueuedOrSent()
    {
        var relay = Create(key: null);

        relay.Record(Entry());

        Assert.False(relay.Enabled);
        Assert.False(File.Exists(QueuePath));
        Assert.False(await relay.FlushAsync());
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task Flush_PostsTheQueueWithKeyAndEnvelope_ThenEmptiesIt()
    {
        Respond();
        var relay = Create();
        relay.Record(Entry("first"));
        relay.Record(Entry("second", "Fatal"));

        Assert.True(await relay.FlushAsync());

        var request = Assert.Single(_handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/" + MobileDiagnosticsContract.LogsPath, request.Uri!.AbsolutePath);
        Assert.Equal("test-relay-key-0123456789", Assert.Single(_presentedKeys));

        var body = Body(0);
        Assert.Equal("ios", body.Platform);
        Assert.Equal("0.2.304+1512", body.AppVersion);
        Assert.Equal("Apple iPhone17,1", body.Device);
        Assert.Equal("Apple", body.Manufacturer);
        Assert.Equal("iPhone17,1", body.Model);
        Assert.Equal("26.6.1", body.OsVersion);
        Assert.Equal(".NET 10.0.1", body.Runtime);
        Assert.Equal("0123456789abcdef0123456789abcdef", body.ModuleVersionId);
        Assert.Equal("abc123", body.InstallId);
        Assert.Collection(
            body.Entries,
            e =>
            {
                Assert.Equal("first", e.Message);
                Assert.Equal("Error", e.Level);
                Assert.StartsWith("System.InvalidOperationException", e.Exception);
                Assert.Equal("System.InvalidOperationException", e.ExceptionType);
                Assert.Equal("boom", e.ExceptionMessage);
                Assert.Equal("CardiTrack.Mobile.Unhandled", e.Source);
                Assert.Equal("SignInPage", e.Screen);
                Assert.Equal("Internet", e.NetworkAccess);
                Assert.Equal(118.2, e.UptimeSeconds);
                Assert.True(e.IsMainThread);
                var frame = Assert.Single(e.Frames);
                Assert.Equal("CardiTrack.Mobile.SignInPage", frame.Type);
                Assert.Equal(31, frame.IlOffset);
                Assert.Equal("0x1033795b8", frame.NativeIp);
            },
            e =>
            {
                Assert.Equal("second", e.Message);
                Assert.Equal("Fatal", e.Level);
            });

        Assert.False(File.Exists(QueuePath));

        // Nothing left: no second request.
        Assert.True(await relay.FlushAsync());
        Assert.Single(_handler.Requests);
    }

    [Fact]
    public async Task TransportFailure_KeepsTheQueueForNextTime()
    {
        _handler.Throws(new HttpRequestException("offline"));
        var relay = Create();
        relay.Record(Entry());

        Assert.False(await relay.FlushAsync());
        Assert.True(File.Exists(QueuePath));

        Respond();
        Assert.True(await relay.FlushAsync());
        Assert.Equal(2, _handler.Requests.Count);
        Assert.Equal("boom", Assert.Single(Body(1).Entries).Message);
    }

    [Fact]
    public async Task ServerError_KeepsTheQueue()
    {
        Respond(HttpStatusCode.ServiceUnavailable);
        var relay = Create();
        relay.Record(Entry());

        Assert.False(await relay.FlushAsync());
        Assert.True(File.Exists(QueuePath));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task ConfigurationRefusal_DropsTheQueueAndStopsRetrying(HttpStatusCode status)
    {
        Respond(status);
        var relay = Create();
        relay.Record(Entry());

        Assert.False(await relay.FlushAsync());
        Assert.False(File.Exists(QueuePath));

        // Even with the server now willing, this process has given up.
        Respond();
        relay.Record(Entry("later"));
        Assert.False(await relay.FlushAsync());
        Assert.Single(_handler.Requests);
    }

    [Fact]
    public async Task BadRequest_DropsThatBatchOnly()
    {
        Respond(HttpStatusCode.BadRequest);
        var relay = Create();
        relay.Record(Entry());

        Assert.True(await relay.FlushAsync());
        Assert.False(File.Exists(QueuePath));

        // Not a refusal: a later entry is still sent.
        Respond();
        relay.Record(Entry("later"));
        Assert.True(await relay.FlushAsync());
        Assert.Equal(2, _handler.Requests.Count);
    }

    [Fact]
    public async Task Queue_KeepsOnlyTheNewestEntries_AndSendsInBatches()
    {
        Respond();
        var relay = Create();
        var total = MobileDiagnosticsRelay.MaxQueuedEntries + 25;
        for (var i = 0; i < total; i++)
            relay.Record(Entry($"entry {i}"));

        Assert.True(await relay.FlushAsync());

        var sent = _handler.Requests.Select((_, i) => Body(i)).SelectMany(b => b.Entries).ToList();
        Assert.Equal(MobileDiagnosticsRelay.MaxQueuedEntries, sent.Count);
        Assert.Equal("entry 25", sent[0].Message);
        Assert.Equal($"entry {total - 1}", sent[^1].Message);
        Assert.All(_handler.Requests.Select((_, i) => Body(i)),
            b => Assert.True(b.Entries.Count <= MobileDiagnosticsContract.MaxEntriesPerBatch));
        Assert.Equal(
            MobileDiagnosticsRelay.MaxQueuedEntries / MobileDiagnosticsContract.MaxEntriesPerBatch,
            _handler.Requests.Count);
    }

    /// <summary>
    /// Text is cut from the front except the log tail, whose newest lines are its last ones —
    /// a crash entry's recent log must end with what happened just before the crash.
    /// </summary>
    [Fact]
    public async Task Record_CutsToTheContractAndNormalisesTheLevel()
    {
        Respond();
        var relay = Create();
        var recentLog = string.Concat(Enumerable.Range(0, 3000).Select(i => $"line {i:D5}\n"));
        relay.Record(new MobileDiagnosticsLogEntry
        {
            Timestamp = DateTimeOffset.UtcNow,
            Level = "Critical",
            Message = new string('m', MobileDiagnosticsContract.MaxMessageLength + 500),
            Exception = new string('e', MobileDiagnosticsContract.MaxExceptionLength + 500),
            Source = new string('s', MobileDiagnosticsContract.MaxFieldLength + 5),
            Frames = Enumerable.Range(0, MobileDiagnosticsContract.MaxFrames + 20)
                .Select(i => new MobileDiagnosticsStackFrame
                {
                    Index = i,
                    Method = new string('f', MobileDiagnosticsContract.MaxFrameFieldLength + 9),
                })
                .ToList(),
            RecentLog = recentLog,
        });

        Assert.True(await relay.FlushAsync());

        var entry = Assert.Single(Body(0).Entries);
        Assert.Equal("Fatal", entry.Level);
        Assert.Equal(MobileDiagnosticsContract.MaxMessageLength, entry.Message.Length);
        Assert.Equal(MobileDiagnosticsContract.MaxExceptionLength, entry.Exception!.Length);
        Assert.Equal(MobileDiagnosticsContract.MaxFieldLength, entry.Source!.Length);
        Assert.Equal(MobileDiagnosticsContract.MaxFrames, entry.Frames.Count);
        Assert.Equal(0, entry.Frames[0].Index);
        Assert.Equal(MobileDiagnosticsContract.MaxFrameFieldLength, entry.Frames[0].Method!.Length);
        Assert.Equal(MobileDiagnosticsContract.MaxRecentLogLength, entry.RecentLog!.Length);
        Assert.EndsWith("line 02999\n", entry.RecentLog);
    }

    [Fact]
    public void TryFlushBlocking_SendsWithinTheBudget()
    {
        Respond();
        var relay = Create();
        relay.Record(Entry());

        Assert.True(relay.TryFlushBlocking(TimeSpan.FromSeconds(10)));
        Assert.Single(_handler.Requests);
        Assert.False(File.Exists(QueuePath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }
}
