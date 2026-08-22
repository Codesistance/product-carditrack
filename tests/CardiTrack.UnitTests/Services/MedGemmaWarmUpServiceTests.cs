using CardiTrack.Application.Interfaces.Clients;
using CardiTrack.Infrastructure.Services;
using CardiTrack.Infrastructure.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// The two guards that keep an app-open from turning into a bill: one warm-up at a time, and no
/// second one for a while after — including after a failed one, which is the case that would
/// otherwise let an unreachable model host be re-dialled on every launch.
/// </summary>
public class MedGemmaWarmUpServiceTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task RequestWarmUp_LoadsTheModel()
    {
        var client = new RecordingWarmUpClient();
        var service = Create(client, out _, out _);

        service.RequestWarmUp();

        await SettleAsync(service);
        Assert.Equal(1, client.Calls);
    }

    [Fact]
    public void RequestWarmUp_DoesNothing_WhenWarmUpIsTurnedOff()
    {
        var client = new RecordingWarmUpClient();
        var service = Create(client, out _, out var settings, configure: s => s.WarmUpEnabled = false);

        service.RequestWarmUp();

        Assert.False(settings.WarmUpEnabled);
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task RequestWarmUp_CollapsesASimultaneousRush_IntoOneLoad()
    {
        var client = new RecordingWarmUpClient { Block = true };
        var service = Create(client, out _, out _);

        for (var i = 0; i < 20; i++)
            service.RequestWarmUp();

        await client.WaitForCallAsync();
        Assert.Equal(1, client.Calls);

        client.Release();
    }

    [Fact]
    public async Task RequestWarmUp_SkipsASecondLoad_InsideTheMinimumInterval()
    {
        var client = new RecordingWarmUpClient();
        var service = Create(client, out var time, out _);

        service.RequestWarmUp();
        await SettleAsync(service);

        time.Advance(TimeSpan.FromSeconds(299));
        service.RequestWarmUp();

        await SettleAsync(service);
        Assert.Equal(1, client.Calls);
    }

    [Fact]
    public async Task RequestWarmUp_LoadsAgain_OnceTheIntervalHasPassed()
    {
        var client = new RecordingWarmUpClient();
        var service = Create(client, out var time, out _);

        service.RequestWarmUp();
        await SettleAsync(service);

        time.Advance(TimeSpan.FromSeconds(301));
        service.RequestWarmUp();

        await SettleAsync(service);
        Assert.Equal(2, client.Calls);
    }

    /// <summary>
    /// The case that matters most for a host that is simply down: a failure must start the same
    /// clock a success does. Without it every arriving caregiver would dial an unreachable
    /// service, and the app-open path would become a load generator pointed at the outage.
    /// </summary>
    [Fact]
    public async Task RequestWarmUp_BacksOffAfterAFailure_TheSameWayItDoesAfterASuccess()
    {
        var client = new RecordingWarmUpClient { Fail = true };
        var service = Create(client, out var time, out _);

        service.RequestWarmUp();
        await SettleAsync(service);

        time.Advance(TimeSpan.FromSeconds(299));
        service.RequestWarmUp();

        await SettleAsync(service);
        Assert.Equal(1, client.Calls);
    }

    [Fact]
    public async Task RequestWarmUp_ReportsAFailureAsAWarning_AndLetsNothingEscape()
    {
        var client = new RecordingWarmUpClient { Fail = true };
        var service = Create(client, out _, out _, out var logger);

        service.RequestWarmUp();

        await SettleAsync(service);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("warm-up failed", entry.Message);
    }

    /// <summary>
    /// Waits for the attempt the service just started, if it started one. The production path
    /// never waits for it — that is the point of the thing — so <c>InFlight</c> is how a test
    /// observes what an attempt left behind rather than racing it.
    /// </summary>
    private static async Task SettleAsync(MedGemmaWarmUpService service)
    {
        if (service.InFlight is { } attempt)
            await attempt.WaitAsync(WaitLimit);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + WaitLimit;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The condition was not met in time.");
            await Task.Delay(10);
        }
    }

    private static MedGemmaWarmUpService Create(
        RecordingWarmUpClient client,
        out AdvanceableTimeProvider time,
        out PrivateAiSettings settings,
        Action<PrivateAiSettings>? configure = null)
        => Create(client, out time, out settings, out _, configure);

    private static MedGemmaWarmUpService Create(
        RecordingWarmUpClient client,
        out AdvanceableTimeProvider time,
        out PrivateAiSettings settings,
        out ListLogger logger,
        Action<PrivateAiSettings>? configure = null)
    {
        // A real container rather than a substituted scope factory: the service resolves its
        // client from a scope of its own, and that is the part worth exercising.
        var scopeFactory = new ServiceCollection()
            .AddScoped<IAiWarmUpClient>(_ => client)
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();

        settings = new PrivateAiSettings
        {
            Model = "medgemma",
            BaseUrl = "http://localhost:11434",
            WarmUpMinimumIntervalSeconds = 300,
        };
        configure?.Invoke(settings);
        time = new AdvanceableTimeProvider();
        logger = new ListLogger();
        return new MedGemmaWarmUpService(scopeFactory, settings, logger, time);
    }

    /// <summary>
    /// Records calls and, optionally, holds one open so a rush can be observed mid-flight.
    /// </summary>
    private sealed class RecordingWarmUpClient : IAiWarmUpClient
    {
        private readonly TaskCompletionSource _released =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Lock _gate = new();
        private int _calls;

        public bool Block { get; init; }

        public bool Fail { get; init; }

        public int Calls { get { lock (_gate) return _calls; } }

        public void Release() => _released.TrySetResult();

        public async Task WarmUpAsync(CancellationToken ct = default)
        {
            lock (_gate) _calls++;

            if (Block)
                await _released.Task.WaitAsync(WaitLimit, ct);

            if (Fail)
                throw new HttpRequestException("MedGemma warm_up returned HTTP 503.");
        }

        public async Task WaitForCallAsync(int calls = 1) => await WaitUntilAsync(() => Calls >= calls);
    }

    /// <summary>
    /// Real time for everything the service does not decide with it — the warm-up's own deadline
    /// is a timer, and only the debounce window reads the clock.
    /// </summary>
    private sealed class AdvanceableTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 8, 22, 9, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow()
        {
            lock (this) return _now;
        }

        public void Advance(TimeSpan by)
        {
            lock (this) _now += by;
        }
    }

    /// <summary>Hand-rolled recording logger, matching the suite's no-mocking-library style.</summary>
    private sealed class ListLogger : ILogger<MedGemmaWarmUpService>
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
