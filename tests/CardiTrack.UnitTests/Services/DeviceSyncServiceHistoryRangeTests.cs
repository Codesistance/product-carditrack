using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services.Notifications;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.ExternalClients;
using CardiTrack.Infrastructure.Services;
using CardiTrack.Infrastructure.Settings;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// <see cref="DeviceSyncService.PullHistoryRangeAsync"/> — the engine behind a caregiver's
/// history re-pull. What matters is the contract the Worker relies on: the exact days, the
/// order, what is stored and what is not, and that nothing about the connection's own
/// schedule or status moves.
/// </summary>
public class DeviceSyncServiceHistoryRangeTests
{
    private readonly IOAuthTokenRefreshService _tokenRefresh = Substitute.For<IOAuthTokenRefreshService>();
    private readonly IDeviceApiClient _deviceApi = Substitute.For<IDeviceApiClient>();
    private readonly IDeviceConnectionRepository _deviceConnections = Substitute.For<IDeviceConnectionRepository>();
    private readonly IDeviceActivityLogRepository _deviceActivityLogs = Substitute.For<IDeviceActivityLogRepository>();
    private readonly IActivityLogAggregationService _aggregation = Substitute.For<IActivityLogAggregationService>();
    private readonly IGranularIngestionService _granularIngestion = Substitute.For<IGranularIngestionService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly INotificationGapResolver _gapResolver = Substitute.For<INotificationGapResolver>();

    private readonly DeviceConnection _connection = new()
    {
        Id = Guid.NewGuid(),
        CardiMemberId = Guid.NewGuid(),
        DeviceType = DeviceType.Fitbit,
        ConnectionStatus = ConnectionStatus.Connected,
        IsActive = true,
    };

    private static readonly DateOnly From = new(2026, 8, 26);
    private static readonly DateOnly To = new(2026, 9, 1);

    public DeviceSyncServiceHistoryRangeTests()
    {
        _tokenRefresh.RefreshIfExpiredAsync(Arg.Any<DeviceConnection>(), Arg.Any<DeviceProviderSettings>())
            .Returns("access_token");
        _deviceApi.GetHealthSnapshotAsync(Arg.Any<string>(), Arg.Any<DateOnly>())
            .Returns(Snapshot());
        _deviceApi.GetGranularDayAsync(Arg.Any<string>(), Arg.Any<DateOnly>())
            .Returns(DeviceGranularDay.Empty);
    }

    private DeviceSyncService CreateSut()
    {
        var config = new DeviceProviderSettings
        {
            Provider = "GoogleHealth",
            DeviceTypes = ["Fitbit", "GooglePixelWatch"],
        };
        return new DeviceSyncService(
            _tokenRefresh, _deviceApi, _deviceConnections, _deviceActivityLogs,
            _aggregation, _granularIngestion, _unitOfWork, _gapResolver,
            Options.Create(new List<DeviceProviderSettings> { config }));
    }

    private static DeviceHealthSnapshot Snapshot(int? steps = 8000) =>
        new(Steps: steps, DistanceKm: null, ActiveMinutes: null, SedentaryMinutes: null,
            Floors: null, CaloriesBurned: null,
            RestingHeartRate: null, AvgHeartRate: null, MaxHeartRate: null, MinHeartRate: null,
            TotalSleepMinutes: null, SleepEfficiency: null,
            SleepStartTime: null, SleepEndTime: null,
            DeepSleepMinutes: null, LightSleepMinutes: null, RemSleepMinutes: null, AwakeMinutes: null);

    [Fact]
    public async Task PullHistoryRange_FetchesEveryDayInTheRange_NewestFirst()
    {
        var fetched = new List<DateOnly>();
        _deviceApi.GetHealthSnapshotAsync(Arg.Any<string>(), Arg.Do<DateOnly>(fetched.Add))
            .Returns(Snapshot());

        await CreateSut().PullHistoryRangeAsync(_connection, From, To);

        Assert.Equal(
        [
            new DateOnly(2026, 9, 1), new DateOnly(2026, 8, 31), new DateOnly(2026, 8, 30),
            new DateOnly(2026, 8, 29), new DateOnly(2026, 8, 28), new DateOnly(2026, 8, 27),
            new DateOnly(2026, 8, 26),
        ], fetched);
    }

    [Fact]
    public async Task PullHistoryRange_StoresOnlyDaysWithData_AndReturnsThatCount()
    {
        // Two empty days in the middle: nothing written for them, and nothing already stored
        // for them touched — a re-pull fills, it never erases.
        _deviceApi.GetHealthSnapshotAsync(Arg.Any<string>(), new DateOnly(2026, 8, 29)).Returns(Snapshot(steps: null));
        _deviceApi.GetHealthSnapshotAsync(Arg.Any<string>(), new DateOnly(2026, 8, 28)).Returns(Snapshot(steps: null));

        var daysWithData = await CreateSut().PullHistoryRangeAsync(_connection, From, To);

        Assert.Equal(5, daysWithData);
        await _deviceActivityLogs.Received(5).UpsertAsync(Arg.Any<DeviceActivityLog>());
        await _deviceActivityLogs.DidNotReceive().UpsertAsync(Arg.Is<DeviceActivityLog>(l => l.Date == new DateOnly(2026, 8, 29)));
        await _aggregation.Received(5).RecomputeAsync(_connection.CardiMemberId, Arg.Any<DateOnly>());
    }

    [Fact]
    public async Task PullHistoryRange_SkipsTheGranularSeries_ForADayWithNoDailyRow()
    {
        // An hour vector must never exist without its daily parent — and an empty day should
        // not cost the five granular requests either.
        _deviceApi.GetHealthSnapshotAsync(Arg.Any<string>(), new DateOnly(2026, 8, 29)).Returns(Snapshot(steps: null));

        await CreateSut().PullHistoryRangeAsync(_connection, From, To);

        await _deviceApi.Received(6).GetGranularDayAsync(Arg.Any<string>(), Arg.Any<DateOnly>());
        await _deviceApi.DidNotReceive().GetGranularDayAsync(Arg.Any<string>(), new DateOnly(2026, 8, 29));
    }

    [Fact]
    public async Task PullHistoryRange_IngestsTheGranularSeries_ForDaysThatHaveThem()
    {
        var granular = new DeviceGranularDay(
            [new GranularSample(new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc), 72)], [], [], [], []);
        _deviceApi.GetGranularDayAsync(Arg.Any<string>(), new DateOnly(2026, 9, 1)).Returns(granular);

        await CreateSut().PullHistoryRangeAsync(_connection, From, To);

        await _deviceApi.Received(7).GetGranularDayAsync(Arg.Any<string>(), Arg.Any<DateOnly>());
        await _granularIngestion.Received(1).IngestDayAsync(_connection, granular, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PullHistoryRange_RefreshesTheTokenOnce_AndStampsNothingOnTheConnection()
    {
        await CreateSut().PullHistoryRangeAsync(_connection, From, To);

        await _tokenRefresh.Received(1).RefreshIfExpiredAsync(_connection, Arg.Any<DeviceProviderSettings>());
        await _deviceConnections.DidNotReceiveWithAnyArgs().MarkSyncSucceededAsync(default, default);
        await _deviceConnections.DidNotReceiveWithAnyArgs().UpdateHistoryBackfilledToAsync(default, default);
    }

    [Fact]
    public async Task PullHistoryRange_ProviderFailure_PropagatesWithoutASyncErrorTransition()
    {
        _deviceApi.GetHealthSnapshotAsync(Arg.Any<string>(), new DateOnly(2026, 8, 30))
            .ThrowsAsync(new GoogleHealthApiException(503, "upstream"));

        await Assert.ThrowsAsync<GoogleHealthApiException>(
            () => CreateSut().PullHistoryRangeAsync(_connection, From, To));

        // The two newer days landed before the failure; the connection's status is untouched.
        await _deviceActivityLogs.Received(2).UpsertAsync(Arg.Any<DeviceActivityLog>());
        await _deviceConnections.DidNotReceiveWithAnyArgs().UpdateStatusAsync(default, default);
    }

    [Fact]
    public async Task PullHistoryRange_RefusesAnInvertedRange()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => CreateSut().PullHistoryRangeAsync(_connection, To, From));
    }
}
