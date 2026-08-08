using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
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
    private readonly IDeviceActivityLogRepository _deviceActivityLogs = Substitute.For<IDeviceActivityLogRepository>();
    private readonly IActivityLogAggregationService _aggregation = Substitute.For<IActivityLogAggregationService>();
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

    // Computed per access, not captured once at type initialization: the service derives its own
    // "yesterday" when it runs, so a cached value would disagree with it if the suite crosses UTC
    // midnight between class load and the assertion.
    private static DateOnly Yesterday => DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1));

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
            _tokenRefresh, _deviceApi, _deviceConnections, _deviceActivityLogs,
            _aggregation, _unitOfWork, options);
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

        await _deviceActivityLogs.Received(1).UpsertAsync(Arg.Is<DeviceActivityLog>(log =>
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
            await _deviceActivityLogs.Received(1).UpsertAsync(Arg.Is<DeviceActivityLog>(log =>
                log != null && log.Date == expected));
        }

        await _deviceActivityLogs.Received(LookbackDays).UpsertAsync(Arg.Any<DeviceActivityLog>());
    }

    [Fact]
    public async Task SyncCardiMemberAsync_FetchesOnlyYesterday_WhenLookbackIsOne()
    {
        _fitbitConfig.SyncLookbackDays = 1;
        SetupSuccessfulTokenRefresh();
        SetupDefaultApiResponse();

        await CreateSut().SyncCardiMemberAsync(_fitbitConnection);

        await _deviceActivityLogs.Received(1).UpsertAsync(Arg.Is<DeviceActivityLog>(log =>
            log != null && log.Date == Yesterday));
        await _deviceActivityLogs.Received(1).UpsertAsync(Arg.Any<DeviceActivityLog>());
    }

    // The raw row is the sync's output; the ActivityLogs row readers consume is derived from it,
    // so every synced day must trigger a recompute for that member-day.
    [Fact]
    public async Task SyncCardiMemberAsync_RecomputesTheMergedRow_ForEveryDaySynced()
    {
        SetupSuccessfulTokenRefresh();
        SetupDefaultApiResponse();

        await CreateSut().SyncCardiMemberAsync(_fitbitConnection);

        for (var offset = 0; offset < LookbackDays; offset++)
        {
            var expected = Yesterday.AddDays(-offset);
            await _aggregation.Received(1)
                .RecomputeAsync(_fitbitConnection.CardiMemberId, expected);
        }
    }

    // The merge reads stored rows, so the raw row has to be saved before the recompute runs.
    [Fact]
    public async Task SyncCardiMemberAsync_SavesTheRawRow_BeforeRecomputing()
    {
        // One day, so the asserted order is the whole call sequence.
        _fitbitConfig.SyncLookbackDays = 1;
        SetupSuccessfulTokenRefresh();
        SetupDefaultApiResponse();

        await CreateSut().SyncCardiMemberAsync(_fitbitConnection);

        Received.InOrder(() =>
        {
            _deviceActivityLogs.UpsertAsync(Arg.Any<DeviceActivityLog>());
            _unitOfWork.SaveChangesAsync();
            _aggregation.RecomputeAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>());
            _unitOfWork.SaveChangesAsync();
        });
    }

    [Fact]
    public async Task SyncCardiMemberAsync_RecordsTheSuccessfulSync_OnSuccess()
    {
        SetupSuccessfulTokenRefresh();
        SetupDefaultApiResponse();

        await CreateSut().SyncCardiMemberAsync(_fitbitConnection);

        await _deviceConnections.Received(1)
            .MarkSyncSucceededAsync(_fitbitConnection.Id, Arg.Any<DateTime>());
    }

    // The recovery half of the SyncError transition below. A connection that errored last run is
    // still pulled, and a window that lands is what puts it back in service — without this it
    // would keep reporting a fault it had already recovered from, and the app would keep telling
    // the user their device needs attention.
    [Fact]
    public async Task SyncCardiMemberAsync_RecordsTheSuccessfulSync_EvenWhenTheConnectionWasInSyncError()
    {
        _fitbitConnection.ConnectionStatus = ConnectionStatus.SyncError;
        SetupSuccessfulTokenRefresh();
        SetupDefaultApiResponse();

        await CreateSut().SyncCardiMemberAsync(_fitbitConnection);

        await _deviceConnections.Received(1)
            .MarkSyncSucceededAsync(_fitbitConnection.Id, Arg.Any<DateTime>());
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
    public async Task SyncCardiMemberAsync_DoesNotRecordSuccess_WhenALaterDayFails()
    {
        SetupSuccessfulTokenRefresh();
        _deviceApi.GetHealthSnapshotAsync(Arg.Any<string>(), Arg.Any<DateOnly>())
            .Returns(Snapshot());
        _deviceApi.GetHealthSnapshotAsync(Arg.Any<string>(), Yesterday)
            .ThrowsAsync(new FitbitApiException(503, "Service Unavailable"));

        await Assert.ThrowsAsync<FitbitApiException>(() =>
            CreateSut().SyncCardiMemberAsync(_fitbitConnection));

        await _deviceConnections.DidNotReceive()
            .MarkSyncSucceededAsync(Arg.Any<Guid>(), Arg.Any<DateTime>());
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

        await _deviceActivityLogs.Received(LookbackDays - 1).UpsertAsync(Arg.Any<DeviceActivityLog>());
        await _aggregation.Received(LookbackDays - 1).RecomputeAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>());
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

    // ── AuditSyncAsync ───────────────────────────────────────────────────────────
    //
    // The audit exists to measure how far back a provider revises data — something a routine sync
    // structurally cannot see, since it only ever looks inside its own window. That makes it an
    // observation: it must reach further back, and it must leave no trace on the connection.

    [Fact]
    public async Task AuditSyncAsync_FetchesTheWiderAuditWindow()
    {
        _fitbitConfig.AuditLookbackDays = 14;
        SetupSuccessfulTokenRefresh();
        SetupDefaultApiResponse();

        await CreateSut().AuditSyncAsync(_fitbitConnection);

        await _deviceApi.Received(14).GetHealthSnapshotAsync(Arg.Any<string>(), Arg.Any<DateOnly>());
        await _deviceActivityLogs.Received(14).UpsertAsync(Arg.Any<DeviceActivityLog>());
    }

    // Narrower than the routine window would make the audit blinder than the thing it checks.
    [Fact]
    public async Task AuditSyncAsync_FallsBackToTheSyncWindow_WhenAuditWindowIsNarrower()
    {
        _fitbitConfig.AuditLookbackDays = 1;
        SetupSuccessfulTokenRefresh();
        SetupDefaultApiResponse();

        await CreateSut().AuditSyncAsync(_fitbitConnection);

        await _deviceApi.Received(LookbackDays).GetHealthSnapshotAsync(Arg.Any<string>(), Arg.Any<DateOnly>());
    }

    // Stamping LastSyncDate would push the connection's next routine pull out by a whole interval,
    // so a job that only measures would silently change what gets collected. The status reset the
    // same call carries is withheld for the same reason: the audit reads a stale window, so it is
    // not evidence about the connection's health either way.
    [Fact]
    public async Task AuditSyncAsync_DoesNotRecordASuccessfulSync()
    {
        SetupSuccessfulTokenRefresh();
        SetupDefaultApiResponse();

        await CreateSut().AuditSyncAsync(_fitbitConnection);

        await _deviceConnections.DidNotReceive()
            .MarkSyncSucceededAsync(Arg.Any<Guid>(), Arg.Any<DateTime>());
    }

    // A historical day failing says nothing about whether the connection works now. Marking it
    // SyncError would take a healthy device out of service on the strength of a stale window.
    [Fact]
    public async Task AuditSyncAsync_DoesNotMarkSyncError_WhenTheProviderFails()
    {
        SetupSuccessfulTokenRefresh();
        _deviceApi.GetHealthSnapshotAsync(Arg.Any<string>(), Arg.Any<DateOnly>())
            .ThrowsAsync(new FitbitApiException(500, "Internal Server Error"));

        await Assert.ThrowsAsync<FitbitApiException>(() =>
            CreateSut().AuditSyncAsync(_fitbitConnection));

        await _deviceConnections.DidNotReceive()
            .UpdateStatusAsync(Arg.Any<Guid>(), Arg.Any<ConnectionStatus>());
    }

    // Whatever the audit turns up is still merged, so a provider's late correction to a member's
    // history is repaired as a side effect of measuring it.
    [Fact]
    public async Task AuditSyncAsync_RecomputesTheMergedRow_ForEveryDayFetched()
    {
        _fitbitConfig.AuditLookbackDays = 5;
        SetupSuccessfulTokenRefresh();
        SetupDefaultApiResponse();

        await CreateSut().AuditSyncAsync(_fitbitConnection);

        for (var offset = 0; offset < 5; offset++)
        {
            await _aggregation.Received(1)
                .RecomputeAsync(_fitbitConnection.CardiMemberId, Yesterday.AddDays(-offset));
        }
    }
}
