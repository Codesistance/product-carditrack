using System.Net;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.ExternalClients;
using CardiTrack.Infrastructure.Settings;
using CardiTrack.Worker;
using CardiTrack.Worker.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace CardiTrack.UnitTests.Workers;

/// <summary>
/// What the sync sweep says about a connection it could not pull. The distinction worth pinning is
/// between a fault — ours or the provider's, worth an error and a stack — and a wearer having taken
/// their consent back, which the system already handles end to end and which recurs on every tick
/// until somebody reconnects the device by hand.
/// </summary>
public class WearableSyncWorkerTests
{
    private readonly IDeviceConnectionRepository _connections = Substitute.For<IDeviceConnectionRepository>();
    private readonly IDeviceSyncService _sync = Substitute.For<IDeviceSyncService>();

    private readonly DeviceConnection _connection = new()
    {
        Id = Guid.NewGuid(),
        CardiMemberId = Guid.NewGuid(),
        DeviceType = DeviceType.Fitbit,
        ConnectionStatus = ConnectionStatus.Connected,
        IsActive = true,
    };

    public WearableSyncWorkerTests()
        => _connections.GetDueForSyncAsync().Returns([_connection]);

    /// <summary>
    /// A refused grant is settled, not broken: the refresh service has already retired the
    /// connection and the recovery worker is probing it on a backoff. Logging it as an error put a
    /// stack trace and an error-rate bump on the sweep every quarter hour for as long as the device
    /// stayed unreconnected — which is how a genuinely broken sync would have been missed.
    /// </summary>
    [Fact]
    public async Task ExecuteJob_LogsAwaitingReconnection_WhenTheProviderRefusedTheGrant()
    {
        _sync.SyncCardiMemberAsync(_connection, Arg.Any<SyncScope>())
            .ThrowsAsync(new DeviceGrantRejectedException(
                _connection.Id, HttpStatusCode.BadRequest, "Token refresh returned 400."));

        var logger = new ListLogger();
        await CreateWorker(logger).RunAsync(CancellationToken.None);

        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Error);

        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("awaiting reconnection", warning.Message);
        Assert.Null(warning.Exception);
    }

    /// <summary>
    /// Counted apart from the failures so the completion line stays honest — a device waiting on
    /// its owner is not a failed sync, and a run reporting one of each should say so.
    /// </summary>
    [Fact]
    public async Task ExecuteJob_KeepsAwaitingReconnectionOutOfTheFailureCount()
    {
        _sync.SyncCardiMemberAsync(_connection, Arg.Any<SyncScope>())
            .ThrowsAsync(new DeviceGrantRejectedException(
                _connection.Id, HttpStatusCode.Unauthorized, "Token refresh returned 401."));

        var logger = new ListLogger();
        await CreateWorker(logger).RunAsync(CancellationToken.None);

        var complete = Assert.Single(logger.Entries, e => e.Message.StartsWith("WearableSync complete"));
        Assert.Contains("Failed: 0", complete.Message);
        Assert.Contains("Awaiting reconnect: 1", complete.Message);
    }

    /// <summary>
    /// Everything else keeps the error and the stack. The quieting is for one named outcome, not
    /// for whatever the sync happens to throw.
    /// </summary>
    [Fact]
    public async Task ExecuteJob_StillLogsAnError_WhenTheSyncFailsForAnyOtherReason()
    {
        var failure = new InvalidOperationException("Token refresh returned 503.");
        _sync.SyncCardiMemberAsync(_connection, Arg.Any<SyncScope>()).ThrowsAsync(failure);

        var logger = new ListLogger();
        await CreateWorker(logger).RunAsync(CancellationToken.None);

        var error = Assert.Single(logger.Entries, e => e.Level == LogLevel.Error);
        Assert.Same(failure, error.Exception);

        var complete = Assert.Single(logger.Entries, e => e.Message.StartsWith("WearableSync complete"));
        Assert.Contains("Failed: 1", complete.Message);
        Assert.Contains("Awaiting reconnect: 0", complete.Message);
    }

    // ── Harness ─────────────────────────────────────────────────────────────────

    private TestableWorker CreateWorker(ILogger<WearableSyncWorker> logger)
    {
        // A real keyed container, as HistoryRepullWorkerTests does: the engine is resolved through
        // the DeviceType→HealthApi mapping, and that lookup is part of what the sweep does.
        var services = new ServiceCollection();
        services.Configure<List<DeviceProviderSettings>>(list => list.Add(new DeviceProviderSettings
        {
            Provider = "GoogleHealth",
            DeviceTypes = ["Fitbit", "GooglePixelWatch"],
        }));
        services.AddKeyedSingleton(HealthApi.GoogleHealth, _sync);
        services.AddSingleton(_connections);
        var provider = services.BuildServiceProvider();

        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(provider);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        var workerOptions = Substitute.For<IOptionsMonitor<WorkerOptions>>();
        workerOptions.Get(nameof(WearableSyncWorker))
            .Returns(new WorkerOptions { CronExpression = "0 */15 * * * *" });

        return new TestableWorker(workerOptions, scopeFactory, logger);
    }

    /// <summary>
    /// Exposes the protected sweep, so tests drive it directly rather than waiting on the cron.
    /// </summary>
    private sealed class TestableWorker(
        IOptionsMonitor<WorkerOptions> workerOptions,
        IServiceScopeFactory scopeFactory,
        ILogger<WearableSyncWorker> logger)
        : WearableSyncWorker(workerOptions, scopeFactory, logger)
    {
        public Task RunAsync(CancellationToken ct) => ExecuteJobAsync(ct);
    }

    private sealed class ListLogger : ILogger<WearableSyncWorker>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception), exception));
    }
}
