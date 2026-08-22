using CardiTrack.Application.Interfaces.Clients;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Infrastructure.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CardiTrack.Infrastructure.Services;

/// <summary>
/// Turns "a caregiver just arrived" into at most one model load, detached from the request that
/// said so.
/// </summary>
/// <remarks>
/// <para>
/// The problem is in <c>cloud_run.tf</c>: the MedGemma service runs at
/// <c>min_instance_count = 0</c> because an always-warm L4 costs more than it saves, and the
/// price of that is ~54 s spent reading weights off disk on the first call after an idle spell
/// (docs/technical/medgemma_serving_architecture.md §9.1a). The batch passes never notice. The
/// caregiver who opens the app, reads the dashboard and then asks a question does — and the
/// minute or so between those two acts is exactly long enough to hide a load in, if something
/// starts it when the app opens rather than when the question is sent.
/// </para>
/// <para>
/// Two guards keep that from becoming a way to pay for a warm instance by accident. Only one
/// warm-up runs at a time, so a hundred simultaneous arrivals cost one load rather than a
/// hundred queued ones against a service that admits a single request. And a completed attempt
/// blocks the next for <see cref="PrivateAiSettings.WarmUpMinimumIntervalSeconds"/> — including a
/// <em>failed</em> attempt, so an unreachable or misconfigured model host is asked every few
/// minutes instead of on every app open.
/// </para>
/// <para>
/// Both guards are per host instance, deliberately: coordinating them across replicas would need
/// shared state for something whose worst case is a second load that finds the model already
/// resident and returns in milliseconds.
/// </para>
/// </remarks>
public sealed class MedGemmaWarmUpService : IAiWarmUpService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly PrivateAiSettings _settings;
    private readonly ILogger<MedGemmaWarmUpService> _logger;
    private readonly TimeProvider _timeProvider;

    private readonly Lock _gate = new();
    private Task? _inFlight;
    private DateTimeOffset? _lastAttemptEndedAt;

    public MedGemmaWarmUpService(
        IServiceScopeFactory scopeFactory,
        PrivateAiSettings settings,
        ILogger<MedGemmaWarmUpService> logger,
        TimeProvider? timeProvider = null)
    {
        _scopeFactory = scopeFactory;
        _settings = settings;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// The attempt currently running, if any. Exists for tests: the production path deliberately
    /// never waits for this task, so a test that wants to observe what one attempt left behind
    /// has nothing else to wait on.
    /// </summary>
    internal Task? InFlight
    {
        get { lock (_gate) return _inFlight; }
    }

    /// <summary>
    /// How long a warm-up may run before it is abandoned. The same interval that gates the next
    /// attempt, so the two cannot contradict each other: a wedged warm-up can hold the door shut
    /// for exactly as long as a completed one would have.
    /// </summary>
    private TimeSpan MinimumInterval => TimeSpan.FromSeconds(_settings.WarmUpMinimumIntervalSeconds);

    public void RequestWarmUp()
    {
        if (!_settings.WarmUpEnabled)
            return;

        lock (_gate)
        {
            if (_inFlight is { IsCompleted: false })
                return;

            if (_lastAttemptEndedAt is { } endedAt
                && _timeProvider.GetUtcNow() - endedAt < MinimumInterval)
            {
                return;
            }

            // Detached on purpose: the caller is an HTTP request that must not wait ~54 s for
            // something it will never read, and whose own cancellation token dies with it.
            _inFlight = Task.Run(RunAsync);
        }
    }

    private async Task RunAsync()
    {
        var startedAt = _timeProvider.GetUtcNow();
        try
        {
            using var timeout = new CancellationTokenSource(MinimumInterval, _timeProvider);
            await using var scope = _scopeFactory.CreateAsyncScope();
            var client = scope.ServiceProvider.GetRequiredService<IAiWarmUpClient>();
            await client.WarmUpAsync(timeout.Token);

            _logger.LogInformation(
                "MedGemma warm-up completed in {ElapsedMs} ms",
                (long)(_timeProvider.GetUtcNow() - startedAt).TotalMilliseconds);
        }
        catch (Exception ex)
        {
            // Warning, not Error: nothing is broken for anyone yet. The next real call makes the
            // same request and will fail loudly there if the host is genuinely down — all this
            // lost is a head start. MedGemmaClient has already logged the HTTP detail, and its
            // exception messages are payload-free by design, so this one is safe to carry.
            _logger.LogWarning(ex,
                "MedGemma warm-up failed after {ElapsedMs} ms; the next call pays the model load as usual",
                (long)(_timeProvider.GetUtcNow() - startedAt).TotalMilliseconds);
        }
        finally
        {
            lock (_gate)
            {
                _lastAttemptEndedAt = _timeProvider.GetUtcNow();
            }
        }
    }
}
