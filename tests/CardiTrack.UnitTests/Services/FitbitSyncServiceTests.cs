using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.ExternalClients;
using CardiTrack.Infrastructure.Services;
using CardiTrack.Infrastructure.Settings;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace CardiTrack.UnitTests.Services;

public class DeviceSyncServiceTests
{
    private readonly IOAuthTokenRefreshService _tokenRefresh = Substitute.For<IOAuthTokenRefreshService>();
    private readonly IDeviceApiClient _deviceApi = Substitute.For<IDeviceApiClient>();
    private readonly IDeviceConnectionRepository _deviceConnections = Substitute.For<IDeviceConnectionRepository>();
    private readonly IActivityLogRepository _activityLogs = Substitute.For<IActivityLogRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();

    private readonly DeviceConnection _fitbitConnection = new()
    {
        Id = Guid.NewGuid(),
        CardiMemberId = Guid.NewGuid(),
        DeviceType = DeviceType.Fitbit,
        ConnectionStatus = ConnectionStatus.Connected,
        IsActive = true
    };

    private const int LookbackDays = 3;

    private static readonly DateOnly Yesterday = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1));

    private readonly DeviceProviderSettings _fitbitConfig = new()
    {
        Provider = "Fitbit",
        ClientId = "test_client",
        ClientSecret = "test_secret",
        TokenUrl = "https://api.fitbit.com/oauth2/token",
        TokenLifetimeHours = 8,
        SyncLookbackDays = LookbackDays
    };

    private DeviceSyncService CreateSut()
    {
        var options = Options.Create(new List<DeviceProviderSettings> { _fitbitConfig });
        return new DeviceSyncService(
            _tokenRefresh, _deviceApi, _deviceConnections, _activityLogs, _unitOfWork, options);
    }

    private static DeviceHealthSnapshot Snapshot(int steps = 8000) =>
        new(Steps: steps, DistanceKm: 5.2m, ActiveMinutes: 45, SedentaryMinutes: 600,
            Floors: 10, CaloriesBurned: 2100,
            RestingHeartRate: 65, AvgHeartRate: 72, MaxHeartRate: 120, MinHeartRate: 55,
            TotalSleepMinutes: 450, SleepEfficiency: 87,
            SleepStartTime: null, SleepEndTime: null,
            DeepSleepMinutes: 90, LightSleepMinutes: 240, RemSleepMinutes: 90, AwakeMinutes: 30);

    private void SetupDefaultApiResponse()
    {
        _deviceApi.GetHealthSnapshotAsync(Arg.Any<string>(), Arg.Any<DateOnly>())
            .Returns(Snapshot());
    }

    private void SetupSuccessfulTokenRefresh()
    {
        _tokenRefresh.RefreshIfExpiredAsync(Arg.Any<DeviceConnection>(), Arg.Any<DeviceProviderSettings>())
            .Returns("access_token");
    }

    [Fact]
    public async Task SyncCardiMemberAsync_CallsTokenRefresh_BeforeFetchingData()
    {
        SetupSuccessfulTokenRefresh();
        SetupDefaultApiResponse();

        await CreateSut().SyncCardiMemberAsync(_fitbitConnection);

        await _tokenRefresh.Received(1).RefreshIfExpiredAsync(_fitbitConnection, _fitbitConfig);
        await _deviceApi.Received(LookbackDays).GetHealthSnapshotAsync(Arg.Any<string>(), Arg.Any<DateOnly>());
    }

    [Fact]
    public async Task SyncCardiMemberAsync_MapsSnapshotToActivityLog_Correctly()
    {
        SetupSuccessfulTokenRefresh();
        SetupDefaultApiResponse();

        await CreateSut().SyncCardiMemberAsync(_fitbitConnection);

        await _activityLogs.Received(1).UpsertAsync(Arg.Is<ActivityLog>(log =>
            log != null &&
            log.Date == Yesterday &&
            log.CardiMemberId == _fitbitConnection.CardiMemberId &&
            log.DeviceConnectionId == _fitbitConnection.Id &&
            log.Steps == 8000 &&
            log.ActiveMinutes == 45 &&
            log.RestingHeartRate == 65 &&
            log.SleepMinutes == 450 &&
            log.SleepEfficiency == 87));
    }

    // A sync that only ever fetched yesterday left a permanent hole for any day the worker was
    // down. The window ends at yesterday and reaches back SyncLookbackDays.
    [Fact]
    public async Task SyncCardiMemberAsync_UpsertsEveryDay_InTheTrailingWindow()
    {
        SetupSuccessfulTokenRefresh();
        SetupDefaultApiResponse();

        await CreateSut().SyncCardiMemberAsync(_fitbitConnection);

        for (var offset = 0; offset < LookbackDays; offset++)
        {
            var expected = Yesterday.AddDays(-offset);
            await _activityLogs.Received(1).UpsertAsync(Arg.Is<ActivityLog>(log =>
                log != null && log.Date == expected));
        }

        await _activityLogs.Received(LookbackDays).UpsertAsync(Arg.Any<ActivityLog>());
    }

    [Fact]
    public async Task SyncCardiMemberAsync_FetchesOnlyYesterday_WhenLookbackIsOne()
    {
        _fitbitConfig.SyncLookbackDays = 1;
        SetupSuccessfulTokenRefresh();
        SetupDefaultApiResponse();

        await CreateSut().SyncCardiMemberAsync(_fitbitConnection);

        await _activityLogs.Received(1).UpsertAsync(Arg.Is<ActivityLog>(log =>
            log != null && log.Date == Yesterday));
        await _activityLogs.Received(1).UpsertAsync(Arg.Any<ActivityLog>());
    }

    [Fact]
    public async Task SyncCardiMemberAsync_UpdatesLastSyncDate_OnSuccess()
    {
        SetupSuccessfulTokenRefresh();
        SetupDefaultApiResponse();

        await CreateSut().SyncCardiMemberAsync(_fitbitConnection);

        await _deviceConnections.Received(1)
            .UpdateLastSyncDateAsync(_fitbitConnection.Id, Arg.Any<DateTime>());
    }

    [Fact]
    public async Task SyncCardiMemberAsync_SetsStatusToSyncError_WhenApiFails()
    {
        SetupSuccessfulTokenRefresh();
        _deviceApi.GetHealthSnapshotAsync(Arg.Any<string>(), Arg.Any<DateOnly>())
            .ThrowsAsync(new FitbitApiException(500, "Internal Server Error"));

        await Assert.ThrowsAsync<FitbitApiException>(() =>
            CreateSut().SyncCardiMemberAsync(_fitbitConnection));

        await _deviceConnections.Received(1)
            .UpdateStatusAsync(_fitbitConnection.Id, ConnectionStatus.SyncError);
    }

    // A partial window must stay due: stamping LastSyncDate would hide the gap until the next
    // interval, and the missing day would never be retried.
    [Fact]
    public async Task SyncCardiMemberAsync_DoesNotUpdateLastSyncDate_WhenALaterDayFails()
    {
        SetupSuccessfulTokenRefresh();
        _deviceApi.GetHealthSnapshotAsync(Arg.Any<string>(), Arg.Any<DateOnly>())
            .Returns(Snapshot());
        _deviceApi.GetHealthSnapshotAsync(Arg.Any<string>(), Yesterday)
            .ThrowsAsync(new FitbitApiException(503, "Service Unavailable"));

        await Assert.ThrowsAsync<FitbitApiException>(() =>
            CreateSut().SyncCardiMemberAsync(_fitbitConnection));

        await _deviceConnections.DidNotReceive()
            .UpdateLastSyncDateAsync(Arg.Any<Guid>(), Arg.Any<DateTime>());
    }

    // Oldest day first, so the days that did come back are already saved when a later one fails.
    [Fact]
    public async Task SyncCardiMemberAsync_KeepsEarlierDays_WhenALaterDayFails()
    {
        SetupSuccessfulTokenRefresh();
        _deviceApi.GetHealthSnapshotAsync(Arg.Any<string>(), Arg.Any<DateOnly>())
            .Returns(Snapshot());
        _deviceApi.GetHealthSnapshotAsync(Arg.Any<string>(), Yesterday)
            .ThrowsAsync(new FitbitApiException(503, "Service Unavailable"));

        await Assert.ThrowsAsync<FitbitApiException>(() =>
            CreateSut().SyncCardiMemberAsync(_fitbitConnection));

        await _activityLogs.Received(LookbackDays - 1).UpsertAsync(Arg.Any<ActivityLog>());
        await _unitOfWork.Received(LookbackDays - 1).SaveChangesAsync();
    }

    [Fact]
    public async Task SyncCardiMemberAsync_DoesNotCallApi_WhenTokenRefreshThrows()
    {
        _tokenRefresh.RefreshIfExpiredAsync(Arg.Any<DeviceConnection>(), Arg.Any<DeviceProviderSettings>())
            .ThrowsAsync(new InvalidOperationException("Refresh failed"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateSut().SyncCardiMemberAsync(_fitbitConnection));

        await _deviceApi.DidNotReceive().GetHealthSnapshotAsync(Arg.Any<string>(), Arg.Any<DateOnly>());
    }
}
