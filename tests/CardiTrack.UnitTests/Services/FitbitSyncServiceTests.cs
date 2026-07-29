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

    private readonly DeviceProviderSettings _fitbitConfig = new()
    {
        Provider = "Fitbit",
        ClientId = "test_client",
        ClientSecret = "test_secret",
        TokenUrl = "https://api.fitbit.com/oauth2/token",
        TokenLifetimeHours = 8
    };

    private DeviceSyncService CreateSut()
    {
        var options = Options.Create(new List<DeviceProviderSettings> { _fitbitConfig });
        return new DeviceSyncService(
            _tokenRefresh, _deviceApi, _deviceConnections, _activityLogs, _unitOfWork, options);
    }

    private void SetupDefaultApiResponse()
    {
        var yesterday = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1));
        _deviceApi.GetHealthSnapshotAsync(Arg.Any<string>(), yesterday)
            .Returns(new DeviceHealthSnapshot(
                Steps: 8000, DistanceKm: 5.2m, ActiveMinutes: 45, SedentaryMinutes: 600,
                Floors: 10, CaloriesBurned: 2100,
                RestingHeartRate: 65, AvgHeartRate: 72, MaxHeartRate: 120, MinHeartRate: 55,
                TotalSleepMinutes: 450, SleepEfficiency: 87,
                SleepStartTime: null, SleepEndTime: null,
                DeepSleepMinutes: 90, LightSleepMinutes: 240, RemSleepMinutes: 90, AwakeMinutes: 30));
    }

    [Fact]
    public async Task SyncCardiMemberAsync_CallsTokenRefresh_BeforeFetchingData()
    {
        _tokenRefresh.RefreshIfExpiredAsync(Arg.Any<DeviceConnection>(), Arg.Any<DeviceProviderSettings>())
            .Returns("access_token");
        SetupDefaultApiResponse();

        await CreateSut().SyncCardiMemberAsync(_fitbitConnection);

        await _tokenRefresh.Received(1).RefreshIfExpiredAsync(_fitbitConnection, _fitbitConfig);
        await _deviceApi.Received(1).GetHealthSnapshotAsync(Arg.Any<string>(), Arg.Any<DateOnly>());
    }

    [Fact]
    public async Task SyncCardiMemberAsync_MapsSnapshotToActivityLog_Correctly()
    {
        _tokenRefresh.RefreshIfExpiredAsync(Arg.Any<DeviceConnection>(), Arg.Any<DeviceProviderSettings>())
            .Returns("access_token");
        SetupDefaultApiResponse();

        await CreateSut().SyncCardiMemberAsync(_fitbitConnection);

        await _activityLogs.Received(1).UpsertAsync(Arg.Is<ActivityLog>(log =>
            log != null &&
            log.Steps == 8000 &&
            log.ActiveMinutes == 45 &&
            log.RestingHeartRate == 65 &&
            log.SleepMinutes == 450 &&
            log.SleepEfficiency == 87));
    }

    [Fact]
    public async Task SyncCardiMemberAsync_UpsertsActivityLog_WithYesterdayDate()
    {
        _tokenRefresh.RefreshIfExpiredAsync(Arg.Any<DeviceConnection>(), Arg.Any<DeviceProviderSettings>())
            .Returns("access_token");
        SetupDefaultApiResponse();

        var expectedDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1));
        await CreateSut().SyncCardiMemberAsync(_fitbitConnection);

        await _activityLogs.Received(1).UpsertAsync(Arg.Is<ActivityLog>(log => log != null && log.Date == expectedDate));
    }

    [Fact]
    public async Task SyncCardiMemberAsync_UpdatesLastSyncDate_OnSuccess()
    {
        _tokenRefresh.RefreshIfExpiredAsync(Arg.Any<DeviceConnection>(), Arg.Any<DeviceProviderSettings>())
            .Returns("access_token");
        SetupDefaultApiResponse();

        await CreateSut().SyncCardiMemberAsync(_fitbitConnection);

        await _deviceConnections.Received(1)
            .UpdateLastSyncDateAsync(_fitbitConnection.Id, Arg.Any<DateTime>());
    }

    [Fact]
    public async Task SyncCardiMemberAsync_SetsStatusToSyncError_WhenApiFails()
    {
        _tokenRefresh.RefreshIfExpiredAsync(Arg.Any<DeviceConnection>(), Arg.Any<DeviceProviderSettings>())
            .Returns("access_token");
        var yesterday = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1));
        _deviceApi.GetHealthSnapshotAsync(Arg.Any<string>(), yesterday)
            .ThrowsAsync(new FitbitApiException(500, "Internal Server Error"));

        await Assert.ThrowsAsync<FitbitApiException>(() =>
            CreateSut().SyncCardiMemberAsync(_fitbitConnection));

        await _deviceConnections.Received(1)
            .UpdateStatusAsync(_fitbitConnection.Id, ConnectionStatus.SyncError);
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
