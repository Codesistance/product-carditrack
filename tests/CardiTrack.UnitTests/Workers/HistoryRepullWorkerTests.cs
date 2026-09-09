using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.ExternalClients;
using CardiTrack.Infrastructure.Settings;
using CardiTrack.Worker;
using CardiTrack.Worker.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace CardiTrack.UnitTests.Workers;

/// <summary>
/// The Worker side of a caregiver's history re-pull. What is worth pinning is the bookkeeping
/// the caregiver's card and the API's gates both read: one chunk per tick, progress written
/// after each, completion exactly when the range is done, a bounded retry before giving up,
/// and a request dropped — not failed — when the member is paused or the device is gone.
/// </summary>
public class HistoryRepullWorkerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 9, 12, 6, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 9, 9);

    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IDeviceHistoryRepullRepository _repulls = Substitute.For<IDeviceHistoryRepullRepository>();
    private readonly IDeviceConnectionRepository _connections = Substitute.For<IDeviceConnectionRepository>();
    private readonly ICardiMemberRepository _members = Substitute.For<ICardiMemberRepository>();
    private readonly IDeviceSyncService _sync = Substitute.For<IDeviceSyncService>();
    private readonly ITimeSeriesPartitionService _partitions = Substitute.For<ITimeSeriesPartitionService>();

    private readonly DeviceConnection _connection;
    private readonly CardiMember _member;

    public HistoryRepullWorkerTests()
    {
        _unitOfWork.DeviceHistoryRepulls.Returns(_repulls);
        _unitOfWork.DeviceConnections.Returns(_connections);
        _unitOfWork.CardiMembers.Returns(_members);

        _member = new CardiMember { Id = Guid.NewGuid(), Name = "Margaret Doe", IsActive = true };
        _connection = new DeviceConnection
        {
            Id = Guid.NewGuid(),
            CardiMemberId = _member.Id,
            DeviceType = DeviceType.Fitbit,
            ConnectionStatus = ConnectionStatus.Connected,
            IsActive = true,
        };
        _connections.GetByIdAsync(_connection.Id).Returns(_connection);
        _members.GetByIdAsync(_member.Id).Returns(_member);

        _sync.PullHistoryRangeAsync(Arg.Any<DeviceConnection>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(7);
    }

    private DeviceHistoryRepull Stage(int days, HistoryRepullStatus status = HistoryRepullStatus.Pending, DateOnly? completedTo = null)
    {
        var (from, to) = HistoryRepullWindow.Bounds(Today, days);
        var repull = new DeviceHistoryRepull
        {
            DeviceConnectionId = _connection.Id,
            CardiMemberId = _member.Id,
            RequestedByUserId = Guid.NewGuid(),
            FromDate = from,
            ToDate = to,
            Status = status,
            CompletedTo = completedTo,
            RequestedAt = Now.UtcDateTime.AddMinutes(-3),
        };
        _repulls.GetDueAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([repull]);
        _repulls.GetByIdAsync(repull.Id).Returns(repull);
        return repull;
    }

    [Fact]
    public async Task Sweep_MarksAPendingRequestStarted_ThenFetchesTheNewestChunk()
    {
        var repull = Stage(30);

        await CreateWorker().RunSweepAsync(CancellationToken.None);

        await _sync.Received(1).PullHistoryRangeAsync(
            _connection, new DateOnly(2026, 9, 2), new DateOnly(2026, 9, 8), Arg.Any<CancellationToken>());
        Assert.Equal(Now.UtcDateTime, repull.StartedAt);
        Assert.Equal(new DateOnly(2026, 9, 2), repull.CompletedTo);
        Assert.Equal(7, repull.DaysWithData);
        Assert.Equal(HistoryRepullStatus.InProgress, repull.Status);
        Assert.Null(repull.CompletedAt);
    }

    [Fact]
    public async Task Sweep_IsMarkedStartedBeforeTheProviderIsCalled()
    {
        // A crash mid-chunk must leave a row that says "started", not one that looks untouched.
        var repull = Stage(30);
        HistoryRepullStatus? statusAtPull = null;
        _sync.PullHistoryRangeAsync(Arg.Any<DeviceConnection>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(_ => { statusAtPull = repull.Status; return 7; });

        await CreateWorker().RunSweepAsync(CancellationToken.None);

        Assert.Equal(HistoryRepullStatus.InProgress, statusAtPull);
    }

    [Fact]
    public async Task Sweep_ContinuesAnInProgressRequest_FromWhereItStopped()
    {
        var repull = Stage(30, HistoryRepullStatus.InProgress, completedTo: new DateOnly(2026, 8, 26));
        repull.DaysWithData = 12;

        await CreateWorker().RunSweepAsync(CancellationToken.None);

        await _sync.Received(1).PullHistoryRangeAsync(
            _connection, new DateOnly(2026, 8, 19), new DateOnly(2026, 8, 25), Arg.Any<CancellationToken>());
        Assert.Equal(new DateOnly(2026, 8, 19), repull.CompletedTo);
        Assert.Equal(19, repull.DaysWithData);
    }

    [Fact]
    public async Task Sweep_CompletesTheRequest_WhenTheLastChunkReachesFromDate()
    {
        var repull = Stage(30, HistoryRepullStatus.InProgress, completedTo: new DateOnly(2026, 8, 12));
        _sync.PullHistoryRangeAsync(Arg.Any<DeviceConnection>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(2);

        await CreateWorker().RunSweepAsync(CancellationToken.None);

        await _sync.Received(1).PullHistoryRangeAsync(
            _connection, new DateOnly(2026, 8, 10), new DateOnly(2026, 8, 11), Arg.Any<CancellationToken>());
        Assert.Equal(HistoryRepullStatus.Completed, repull.Status);
        Assert.Equal(Now.UtcDateTime, repull.CompletedAt);
        Assert.Equal(new DateOnly(2026, 8, 10), repull.CompletedTo);
    }

    [Fact]
    public async Task Sweep_EnsuresPartitionsForTheChunk_BeforePulling()
    {
        Stage(30);
        var ensuredFirst = false;
        _sync.PullHistoryRangeAsync(Arg.Any<DeviceConnection>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                ensuredFirst = _partitions.ReceivedCalls()
                    .Any(c => c.GetMethodInfo().Name == nameof(ITimeSeriesPartitionService.EnsurePartitionsForRangeAsync));
                return 7;
            });

        await CreateWorker().RunSweepAsync(CancellationToken.None);

        await _partitions.Received(1).EnsurePartitionsForRangeAsync(
            new DateOnly(2026, 9, 2), new DateOnly(2026, 9, 8), Arg.Any<CancellationToken>());
        Assert.True(ensuredFirst, "Partitions must exist before the chunk's granular rows are written.");
    }

    [Fact]
    public async Task Sweep_AsksForAtMostMaxPerTickRequests()
    {
        Stage(30);

        await CreateWorker(maxPerTick: 3).RunSweepAsync(CancellationToken.None);

        await _repulls.Received(1).GetDueAsync(3, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Sweep_WithMaxPerTickOff_DoesNothing()
    {
        Stage(30);

        await CreateWorker(maxPerTick: 0).RunSweepAsync(CancellationToken.None);

        await _repulls.DidNotReceiveWithAnyArgs().GetDueAsync(default, default);
        await _sync.DidNotReceiveWithAnyArgs().PullHistoryRangeAsync(default!, default, default, default);
    }

    [Fact]
    public async Task Sweep_CancelsARequest_WhenMonitoringWasPausedAfterItWasQueued()
    {
        var repull = Stage(30);
        _member.MonitoringPausedUntil = Now.UtcDateTime.AddHours(4);

        await CreateWorker().RunSweepAsync(CancellationToken.None);

        Assert.Equal(HistoryRepullStatus.Cancelled, repull.Status);
        Assert.Equal("MONITORING_PAUSED", repull.FailureReason);
        Assert.Equal(Now.UtcDateTime, repull.CompletedAt);
        await _sync.DidNotReceiveWithAnyArgs().PullHistoryRangeAsync(default!, default, default, default);
    }

    [Theory]
    [InlineData(ConnectionStatus.Disconnected)]
    [InlineData(ConnectionStatus.TokenExpired)]
    public async Task Sweep_CancelsARequest_WhenTheConnectionCanNoLongerSync(ConnectionStatus status)
    {
        var repull = Stage(30);
        _connection.ConnectionStatus = status;

        await CreateWorker().RunSweepAsync(CancellationToken.None);

        Assert.Equal(HistoryRepullStatus.Cancelled, repull.Status);
        Assert.Equal("DEVICE_NOT_SYNCABLE", repull.FailureReason);
        await _sync.DidNotReceiveWithAnyArgs().PullHistoryRangeAsync(default!, default, default, default);
    }

    [Fact]
    public async Task Sweep_ProviderFailure_CountsAnAttempt_AndLeavesTheRequestOpen()
    {
        var repull = Stage(30);
        _sync.PullHistoryRangeAsync(Arg.Any<DeviceConnection>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new GoogleHealthApiException(503, "{\"steps\":8000}"));

        await CreateWorker().RunSweepAsync(CancellationToken.None);

        Assert.Equal(HistoryRepullStatus.InProgress, repull.Status);
        Assert.Equal(1, repull.Attempts);
        Assert.Null(repull.CompletedTo);
        // The reason is the type and status, never the body — a provider error can carry readings.
        Assert.Equal("GoogleHealthApiException (HTTP 503)", repull.FailureReason);
        Assert.DoesNotContain("8000", repull.FailureReason);
        await _connections.DidNotReceiveWithAnyArgs().UpdateStatusAsync(default, default);
    }

    [Fact]
    public async Task Sweep_ChunkThatLands_ClearsWhatTheRetriedOnesLeftBehind()
    {
        // Otherwise a request that stumbled once and then finished reads for good as a completed
        // request that also failed, and sits two-thirds of the way to giving up.
        var repull = Stage(30, HistoryRepullStatus.InProgress, completedTo: new DateOnly(2026, 9, 2));
        repull.Attempts = 2;
        repull.FailureReason = "GoogleHealthApiException (HTTP 503)";

        await CreateWorker().RunSweepAsync(CancellationToken.None);

        Assert.Null(repull.FailureReason);
        Assert.Equal(0, repull.Attempts);
        Assert.Equal(new DateOnly(2026, 8, 26), repull.CompletedTo);
    }

    [Fact]
    public async Task Sweep_GivesUp_OnTheThirdFailedAttempt()
    {
        var repull = Stage(30, HistoryRepullStatus.InProgress, completedTo: new DateOnly(2026, 9, 2));
        repull.Attempts = HistoryRepullWorker.MaxAttempts - 1;
        _sync.PullHistoryRangeAsync(Arg.Any<DeviceConnection>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("boom"));

        await CreateWorker().RunSweepAsync(CancellationToken.None);

        Assert.Equal(HistoryRepullStatus.Failed, repull.Status);
        Assert.Equal(HistoryRepullWorker.MaxAttempts, repull.Attempts);
        Assert.Equal(Now.UtcDateTime, repull.CompletedAt);
        Assert.Equal("InvalidOperationException", repull.FailureReason);
        // What landed before stays: CompletedTo is not rolled back.
        Assert.Equal(new DateOnly(2026, 9, 2), repull.CompletedTo);
    }

    [Fact]
    public async Task Sweep_FailsARequest_WhoseDeviceTypeHasNoSyncEngine()
    {
        var repull = Stage(30);
        _connection.DeviceType = DeviceType.Garmin;

        await CreateWorker().RunSweepAsync(CancellationToken.None);

        Assert.Equal(HistoryRepullStatus.Failed, repull.Status);
        Assert.Equal("NO_SYNC_SERVICE", repull.FailureReason);
    }

    [Fact]
    public async Task Sweep_SkipsARequestTheApiClosedSinceTheDueRead()
    {
        var repull = Stage(30);
        _repulls.GetByIdAsync(repull.Id).Returns(new DeviceHistoryRepull
        {
            Id = repull.Id,
            Status = HistoryRepullStatus.Cancelled,
        });

        await CreateWorker().RunSweepAsync(CancellationToken.None);

        await _sync.DidNotReceiveWithAnyArgs().PullHistoryRangeAsync(default!, default, default, default);
    }

    // ── Harness ─────────────────────────────────────────────────────────────────

    private TestableWorker CreateWorker(int maxPerTick = 5)
    {
        // A real keyed container, as ManualDeviceSyncServiceTests does: resolving the engine
        // through the DeviceType→HealthApi mapping is the part most likely to break.
        var services = new ServiceCollection();
        services.Configure<List<DeviceProviderSettings>>(list => list.Add(new DeviceProviderSettings
        {
            Provider = "GoogleHealth",
            DeviceTypes = ["Fitbit", "GooglePixelWatch"],
            BackfillChunkDays = 7,
        }));
        services.AddKeyedSingleton(HealthApi.GoogleHealth, _sync);
        services.AddSingleton(_unitOfWork);
        services.AddSingleton(_partitions);
        var provider = services.BuildServiceProvider();

        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(provider);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        var workerOptions = Substitute.For<IOptionsMonitor<WorkerOptions>>();
        workerOptions.Get(nameof(HistoryRepullWorker))
            .Returns(new WorkerOptions { CronExpression = "0 6-59/10 * * * *" });

        var options = Substitute.For<IOptionsMonitor<HistoryRepullOptions>>();
        options.CurrentValue.Returns(new HistoryRepullOptions { MaxPerTick = maxPerTick });

        return new TestableWorker(
            workerOptions, options, scopeFactory,
            NullLogger<HistoryRepullWorker>.Instance, new FixedTimeProvider(Now));
    }

    /// <summary>
    /// Exposes the protected sweep, so tests drive it directly rather than through the advisory
    /// lock (which needs a live Postgres connection — the integration suite's territory).
    /// </summary>
    private sealed class TestableWorker(
        IOptionsMonitor<WorkerOptions> workerOptions,
        IOptionsMonitor<HistoryRepullOptions> options,
        IServiceScopeFactory scopeFactory,
        Microsoft.Extensions.Logging.ILogger<HistoryRepullWorker> logger,
        TimeProvider timeProvider)
        : HistoryRepullWorker(workerOptions, options, scopeFactory, logger, timeProvider)
    {
        public Task RunSweepAsync(CancellationToken ct) => SweepAsync(ct);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
