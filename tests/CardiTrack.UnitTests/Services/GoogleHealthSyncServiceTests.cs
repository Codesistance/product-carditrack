using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services;
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
    private readonly IGranularIngestionService _granularIngestion = Substitute.For<IGranularIngestionService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly INotificationGapResolver _gapResolver = Substitute.For<INotificationGapResolver>();

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
    // "today" when it runs, so a cached value would disagree with it if the suite crosses UTC
    // midnight between class load and the assertion.
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    // The window ends at today and reaches back SyncLookbackDays complete days, so it covers
    // LookbackDays + 1 days in total.
    private static int WindowDays(int lookbackDays) => lookbackDays + 1;

    private readonly DeviceProviderSettings _googleHealthConfig = new()
    {
        Provider = "GoogleHealth",
        DeviceTypes = ["Fitbit", "GooglePixelWatch"],
        ClientId = "test_client",
        ClientSecret = "test_secret",
        TokenUrl = "https://api.fitbit.com/oauth2/token",
        TokenLifetimeHours = 8,
        SyncLookbackDays = LookbackDays
    };

    private DeviceSyncService CreateSut()
    {
        var options = Options.Create(new List<DeviceProviderSettings> { _googleHealthConfig });
        return new DeviceSyncService(
            _tokenRefresh, _deviceApi, _deviceConnections, _deviceActivityLogs,
            _aggregation, _granularIngestion, _unitOfWork, _gapResolver, options);
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

        await _tokenRefresh.Received(1).RefreshIfExpiredAsync(_fitbitConnection, _googleHealthConfig);
        await _deviceApi.Received(WindowDays(LookbackDays))
            .GetHealthSnapshotAsync(Arg.Any<string>(), Arg.Any<DateOnly>());
    }

    [Fact]
    public async Task SyncCardiMemberAsync_MapsSnapshotToActivityLog_Correctly()
    {
        SetupSuccessfulTokenRefresh();
        SetupDefaultApiResponse();

        await CreateSut().SyncCardiMemberAsync(_fitbitConnection);

        await _deviceActivityLogs.Received(1).UpsertAsync(Arg.Is<DeviceActivityLog>(log =>
            log != null &&
            log.Date == Today &&
            log.CardiMemberId == _fitbitConnection.CardiMemberId &&
            log.DeviceConnectionId == _fitbitConnection.Id &&
            log.Steps == 8000 &&
            log.ActiveMinutes == 45 &&
            log.RestingHeartRate == 65 &&
            log.SleepMinutes == 450 &&
            log.SleepEfficiency == 87));
    }

    // A sync that only ever fetched yesterday left a permanent hole for any day the worker was
    // down. The window reaches back SyncLookbackDays complete days from today.
    [Fact]
    public async Task SyncCardiMemberAsync_UpsertsEveryDay_InTheTrailingWindow()
    {
        SetupSuccessfulTokenRefresh();
        SetupDefaultApiResponse();

        await CreateSut().SyncCardiMemberAsync(_fitbitConnection);

        for (var offset = 0; offset < WindowDays(LookbackDays); offset++)
        {
            var expected = Today.AddDays(-offset);
            await _deviceActivityLogs.Received(1).UpsertAsync(Arg.Is<DeviceActivityLog>(log =>
                log != null && log.Date == expected));
        }

        await _deviceActivityLogs.Received(WindowDays(LookbackDays)).UpsertAsync(Arg.Any<DeviceActivityLog>());
    }

    // ── The daily repair pass ───────────────────────────────────────────────────────────────

    // At a ten-minute cadence the trailing days would be re-read ~144 times a day per wearer, to
    // find numbers that a finished day cannot have changed. They are worth exactly one pull a day.
    [Fact]
    public async Task SyncCardiMemberAsync_FetchesTodayAlone_WhenTheRepairPassAlreadyRanToday()
    {
        _fitbitConnection.LastSyncDate = DateTime.UtcNow;
        SetupSuccessfulTokenRefresh();
        SetupDefaultApiResponse();

        await CreateSut().SyncCardiMemberAsync(_fitbitConnection);

        // The claim is "one day, not the whole window", asserted as a count. Naming the day would
        // reintroduce a second clock read that disagrees with the service's own across UTC
        // midnight; which day it is belongs to StoresTodayAsWellAsTheCompletedDays.
        await _deviceApi.Received(1).GetHealthSnapshotAsync(Arg.Any<string>(), Arg.Any<DateOnly>());
    }

    // First pull of a new UTC day: the trailing days come back, which is what repairs a provider's
    // overnight revision and any day missed while the puller was down.
    [Fact]
    public async Task SyncCardiMemberAsync_FetchesTheFullWindow_OnTheDaysFirstPull()
    {
        _fitbitConnection.LastSyncDate = DateTime.UtcNow.AddDays(-1);
        SetupSuccessfulTokenRefresh();
        SetupDefaultApiResponse();

        await CreateSut().SyncCardiMemberAsync(_fitbitConnection);

        await _deviceApi.Received(WindowDays(LookbackDays))
            .GetHealthSnapshotAsync(Arg.Any<string>(), Arg.Any<DateOnly>());
    }

    // A connection that has never synced has no history at all, so it takes the full window.
    [Fact]
    public async Task SyncCardiMemberAsync_FetchesTheFullWindow_WhenNeverSynced()
    {
        _fitbitConnection.LastSyncDate = null;
        SetupSuccessfulTokenRefresh();
        SetupDefaultApiResponse();

        await CreateSut().SyncCardiMemberAsync(_fitbitConnection);

        await _deviceApi.Received(WindowDays(LookbackDays))
            .GetHealthSnapshotAsync(Arg.Any<string>(), Arg.Any<DateOnly>());
    }

    // The audit exists to see revisions the routine pull structurally cannot, so it always reaches
    // back regardless of whether today's repair pass has run.
    [Fact]
    public async Task AuditSyncAsync_StillFetchesItsWholeWindow_WhenSyncedToday()
    {
        _fitbitConnection.LastSyncDate = DateTime.UtcNow;
        _googleHealthConfig.AuditLookbackDays = 5;
        SetupSuccessfulTokenRefresh();
        SetupDefaultApiResponse();

        await CreateSut().AuditSyncAsync(_fitbitConnection);

        await _deviceApi.Received(WindowDays(5))
            .GetHealthSnapshotAsync(Arg.Any<string>(), Arg.Any<DateOnly>());
    }

    // The dashboard's Key Metrics read the newest stored day, so a window that stopped at
    // yesterday left them frozen on a completed day however often the caregiver refreshed.
    [Fact]
    public async Task SyncCardiMemberAsync_StoresTodayAsWellAsTheCompletedDays()
    {
        SetupSuccessfulTokenRefresh();
        SetupDefaultApiResponse();

        await CreateSut().SyncCardiMemberAsync(_fitbitConnection);

        await _deviceApi.Received(1).GetHealthSnapshotAsync(Arg.Any<string>(), Today);
        await _deviceActivityLogs.Received(1).UpsertAsync(Arg.Is<DeviceActivityLog>(log =>
            log != null && log.Date == Today));
    }

    [Fact]
    public async Task SyncCardiMemberAsync_FetchesTodayAndYesterday_WhenLookbackIsOne()
    {
        _googleHealthConfig.SyncLookbackDays = 1;
        SetupSuccessfulTokenRefresh();
        SetupDefaultApiResponse();

        await CreateSut().SyncCardiMemberAsync(_fitbitConnection);

        await _deviceActivityLogs.Received(1).UpsertAsync(Arg.Is<DeviceActivityLog>(log =>
            log != null && log.Date == Today));
        await _deviceActivityLogs.Received(1).UpsertAsync(Arg.Is<DeviceActivityLog>(log =>
            log != null && log.Date == Today.AddDays(-1)));
        await _deviceActivityLogs.Received(2).UpsertAsync(Arg.Any<DeviceActivityLog>());
    }

    // The raw row is the sync's output; the ActivityLogs row readers consume is derived from it,
    // so every synced day must trigger a recompute for that member-day.
    [Fact]
    public async Task SyncCardiMemberAsync_RecomputesTheMergedRow_ForEveryDaySynced()
    {
        SetupSuccessfulTokenRefresh();
        SetupDefaultApiResponse();

        await CreateSut().SyncCardiMemberAsync(_fitbitConnection);

        for (var offset = 0; offset < WindowDays(LookbackDays); offset++)
        {
            var expected = Today.AddDays(-offset);
            await _aggregation.Received(1)
                .RecomputeAsync(_fitbitConnection.CardiMemberId, expected);
        }
    }

    // The merge reads stored rows, so the raw row has to be saved before the recompute runs.
    [Fact]
    public async Task SyncCardiMemberAsync_SavesTheRawRow_BeforeRecomputing()
    {
        // The narrowest window the service allows — yesterday and today — so the asserted order
        // is the whole call sequence.
        _googleHealthConfig.SyncLookbackDays = 1;
        SetupSuccessfulTokenRefresh();
        SetupDefaultApiResponse();

        await CreateSut().SyncCardiMemberAsync(_fitbitConnection);

        Received.InOrder(() =>
        {
            _deviceActivityLogs.UpsertAsync(Arg.Any<DeviceActivityLog>());
            _unitOfWork.SaveChangesAsync();
            _aggregation.RecomputeAsync(Arg.Any<Guid>(), Today.AddDays(-1));
            _unitOfWork.SaveChangesAsync();
            _deviceActivityLogs.UpsertAsync(Arg.Any<DeviceActivityLog>());
            _unitOfWork.SaveChangesAsync();
            _aggregation.RecomputeAsync(Arg.Any<Guid>(), Today);
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
            .ThrowsAsync(new GoogleHealthApiException(500, "Internal Server Error"));

        await Assert.ThrowsAsync<GoogleHealthApiException>(() =>
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
        _deviceApi.GetHealthSnapshotAsync(Arg.Any<string>(), Today.AddDays(-1))
            .ThrowsAsync(new GoogleHealthApiException(503, "Service Unavailable"));

        await Assert.ThrowsAsync<GoogleHealthApiException>(() =>
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
        _deviceApi.GetHealthSnapshotAsync(Arg.Any<string>(), Today.AddDays(-1))
            .ThrowsAsync(new GoogleHealthApiException(503, "Service Unavailable"));

        await Assert.ThrowsAsync<GoogleHealthApiException>(() =>
            CreateSut().SyncCardiMemberAsync(_fitbitConnection));

        // The window runs today-LookbackDays..today oldest first, so the days stored before
        // yesterday threw are today-LookbackDays..today-2 — LookbackDays - 1 of them.
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

    // ── History backfill ─────────────────────────────────────────────────────────
    //
    // A fresh connection starts with only the routine window, but the wearable often holds months
    // of history the provider will still serve. The backfill walks that history backwards one
    // chunk per pull — never in one pull, which would blow the per-wearer request ceiling, fail
    // partway, and start over on the next pull without ever completing.

    private static DeviceHealthSnapshot EmptySnapshot() =>
        new(Steps: null, DistanceKm: null, ActiveMinutes: null, SedentaryMinutes: null,
            Floors: null, CaloriesBurned: null,
            RestingHeartRate: null, AvgHeartRate: null, MaxHeartRate: null, MinHeartRate: null,
            TotalSleepMinutes: null, SleepEfficiency: null,
            SleepStartTime: null, SleepEndTime: null,
            DeepSleepMinutes: null, LightSleepMinutes: null, RemSleepMinutes: null, AwakeMinutes: null);

    [Fact]
    public async Task SyncCardiMemberAsync_FetchesOneChunkJustPastTheRoutineWindow_WhenExtendingHistory()
    {
        SetupSuccessfulTokenRefresh();
        SetupDefaultApiResponse();

        await CreateSut().SyncCardiMemberAsync(_fitbitConnection, SyncScope.WorkerCadence);

        // The routine window plus exactly one chunk — never the whole horizon at once.
        await _deviceApi.Received(WindowDays(LookbackDays) + _googleHealthConfig.BackfillChunkDays)
            .GetHealthSnapshotAsync(Arg.Any<string>(), Arg.Any<DateOnly>());

        // The chunk starts on the first day the routine window does not reach.
        for (var offset = LookbackDays + 1; offset <= LookbackDays + _googleHealthConfig.BackfillChunkDays; offset++)
        {
            await _deviceApi.Received(1)
                .GetHealthSnapshotAsync(Arg.Any<string>(), Today.AddDays(-offset));
        }
    }

    // The manual-sync path shares SyncCardiMemberAsync, and a caregiver waiting on a refresh must
    // not pay for a chunk of last month — history extension is the Worker cadence's job.
    [Fact]
    public async Task SyncCardiMemberAsync_DoesNotTouchHistory_ByDefault()
    {
        SetupSuccessfulTokenRefresh();
        SetupDefaultApiResponse();

        await CreateSut().SyncCardiMemberAsync(_fitbitConnection);

        await _deviceApi.Received(WindowDays(LookbackDays))
            .GetHealthSnapshotAsync(Arg.Any<string>(), Arg.Any<DateOnly>());
        await _deviceConnections.DidNotReceive()
            .UpdateHistoryBackfilledToAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>());
    }

    [Fact]
    public async Task SyncCardiMemberAsync_AdvancesTheFrontierPerDay_WhileBackfilling()
    {
        SetupSuccessfulTokenRefresh();
        SetupDefaultApiResponse();

        await CreateSut().SyncCardiMemberAsync(_fitbitConnection, SyncScope.WorkerCadence);

        var chunkEnd = Today.AddDays(-(LookbackDays + _googleHealthConfig.BackfillChunkDays));
        await _deviceConnections.Received(_googleHealthConfig.BackfillChunkDays)
            .UpdateHistoryBackfilledToAsync(_fitbitConnection.Id, Arg.Any<DateOnly>());
        await _deviceConnections.Received(1)
            .UpdateHistoryBackfilledToAsync(_fitbitConnection.Id, chunkEnd);
        Assert.Equal(chunkEnd, _fitbitConnection.HistoryBackfilledTo);
    }

    [Fact]
    public async Task SyncCardiMemberAsync_ResumesBackfillFromTheStoredFrontier()
    {
        _fitbitConnection.HistoryBackfilledTo = Today.AddDays(-30);
        SetupSuccessfulTokenRefresh();
        SetupDefaultApiResponse();

        await CreateSut().SyncCardiMemberAsync(_fitbitConnection, SyncScope.WorkerCadence);

        for (var offset = 31; offset <= 30 + _googleHealthConfig.BackfillChunkDays; offset++)
        {
            await _deviceApi.Received(1)
                .GetHealthSnapshotAsync(Arg.Any<string>(), Today.AddDays(-offset));
        }
    }

    [Fact]
    public async Task SyncCardiMemberAsync_StopsBackfillingAtTheHorizon()
    {
        _googleHealthConfig.BackfillDays = 90;
        _fitbitConnection.HistoryBackfilledTo = Today.AddDays(-88);
        SetupSuccessfulTokenRefresh();
        SetupDefaultApiResponse();

        await CreateSut().SyncCardiMemberAsync(_fitbitConnection, SyncScope.WorkerCadence);

        // Only days 89 and 90 remain of the horizon, chunk size notwithstanding.
        await _deviceApi.Received(WindowDays(LookbackDays) + 2)
            .GetHealthSnapshotAsync(Arg.Any<string>(), Arg.Any<DateOnly>());
        Assert.Equal(Today.AddDays(-90), _fitbitConnection.HistoryBackfilledTo);
    }

    [Fact]
    public async Task SyncCardiMemberAsync_BackfillsNothing_WhenTheHorizonIsAlreadyReached()
    {
        _googleHealthConfig.BackfillDays = 90;
        _fitbitConnection.HistoryBackfilledTo = Today.AddDays(-90);
        SetupSuccessfulTokenRefresh();
        SetupDefaultApiResponse();

        await CreateSut().SyncCardiMemberAsync(_fitbitConnection, SyncScope.WorkerCadence);

        await _deviceApi.Received(WindowDays(LookbackDays))
            .GetHealthSnapshotAsync(Arg.Any<string>(), Arg.Any<DateOnly>());
        await _deviceConnections.DidNotReceive()
            .UpdateHistoryBackfilledToAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>());
    }

    [Fact]
    public async Task SyncCardiMemberAsync_BackfillsNothing_WhenBackfillIsDisabled()
    {
        _googleHealthConfig.BackfillDays = 0;
        SetupSuccessfulTokenRefresh();
        SetupDefaultApiResponse();

        await CreateSut().SyncCardiMemberAsync(_fitbitConnection, SyncScope.WorkerCadence);

        await _deviceApi.Received(WindowDays(LookbackDays))
            .GetHealthSnapshotAsync(Arg.Any<string>(), Arg.Any<DateOnly>());
        await _deviceConnections.DidNotReceive()
            .UpdateHistoryBackfilledToAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>());
    }

    // An all-null row would read as a "data day" to the baseline coverage gate and the dashboard's
    // days-captured figure. Checked-and-empty is a final answer, so the frontier still advances.
    [Fact]
    public async Task SyncCardiMemberAsync_ChecksButDoesNotStore_EmptyBackfillDays()
    {
        SetupSuccessfulTokenRefresh();
        var routineFloor = Today.AddDays(-LookbackDays);
        _deviceApi.GetHealthSnapshotAsync(Arg.Any<string>(), Arg.Any<DateOnly>())
            .Returns(call => call.Arg<DateOnly>() >= routineFloor ? Snapshot() : EmptySnapshot());

        await CreateSut().SyncCardiMemberAsync(_fitbitConnection, SyncScope.WorkerCadence);

        // Only the routine window stored; the empty history days were checked and skipped.
        await _deviceActivityLogs.Received(WindowDays(LookbackDays))
            .UpsertAsync(Arg.Any<DeviceActivityLog>());
        await _deviceConnections.Received(_googleHealthConfig.BackfillChunkDays)
            .UpdateHistoryBackfilledToAsync(_fitbitConnection.Id, Arg.Any<DateOnly>());
    }

    // The routine window landed before the chunk started, so its success stands; the frontier
    // records how far the chunk got, and the next pull resumes from there.
    [Fact]
    public async Task SyncCardiMemberAsync_KeepsTheRoutineSuccess_WhenBackfillFails()
    {
        SetupSuccessfulTokenRefresh();
        _deviceApi.GetHealthSnapshotAsync(Arg.Any<string>(), Arg.Any<DateOnly>())
            .Returns(Snapshot());
        _deviceApi.GetHealthSnapshotAsync(Arg.Any<string>(), Today.AddDays(-(LookbackDays + 3)))
            .ThrowsAsync(new GoogleHealthApiException(503, "Service Unavailable"));

        await Assert.ThrowsAsync<GoogleHealthApiException>(() =>
            CreateSut().SyncCardiMemberAsync(_fitbitConnection, SyncScope.WorkerCadence));

        await _deviceConnections.Received(1)
            .MarkSyncSucceededAsync(_fitbitConnection.Id, Arg.Any<DateTime>());
        // Two chunk days completed before the third threw.
        await _deviceConnections.Received(2)
            .UpdateHistoryBackfilledToAsync(_fitbitConnection.Id, Arg.Any<DateOnly>());
        Assert.Equal(Today.AddDays(-(LookbackDays + 2)), _fitbitConnection.HistoryBackfilledTo);
    }

    // ── Granular series (worker cadence) ────────────────────────────────────────
    //
    // Minute-grain series ride the routine window on the worker cadence only: the manual path
    // must stay fast, and backfill days stay daily-grain until the probe verifies how far back
    // the provider serves intraday history.

    private static DeviceGranularDay GranularDayWithData() =>
        new(HeartRate: [new GranularSample(DateTime.UtcNow, 70f)],
            Steps: [], ActiveZoneMinutes: [], SpO2: []);

    [Fact]
    public async Task SyncCardiMemberAsync_FetchesGranular_ForEachRoutineWindowDay_OnWorkerCadence()
    {
        SetupSuccessfulTokenRefresh();
        SetupDefaultApiResponse();
        _deviceApi.GetGranularDayAsync(Arg.Any<string>(), Arg.Any<DateOnly>())
            .Returns(GranularDayWithData());

        await CreateSut().SyncCardiMemberAsync(_fitbitConnection, SyncScope.WorkerCadence);

        await _deviceApi.Received(WindowDays(LookbackDays))
            .GetGranularDayAsync(Arg.Any<string>(), Arg.Any<DateOnly>());
        await _granularIngestion.Received(WindowDays(LookbackDays))
            .IngestDayAsync(_fitbitConnection, Arg.Any<DeviceGranularDay>(), Arg.Any<CancellationToken>());

        // Only after the success stamp: granular is enrichment, and running it inside the
        // success envelope would let a transient granular failure un-succeed daily data.
        // (InOrder needs the spec's multiplicity to match, so every granular call is listed.)
        Received.InOrder(() =>
        {
            _deviceConnections.MarkSyncSucceededAsync(_fitbitConnection.Id, Arg.Any<DateTime>());
            for (var i = 0; i < WindowDays(LookbackDays); i++)
                _deviceApi.GetGranularDayAsync(Arg.Any<string>(), Arg.Any<DateOnly>());
        });
    }

    // The recovery mirror of KeepsTheRoutineSuccess_WhenBackfillFails: the daily window landed,
    // so its success stands whatever the granular fetch does afterwards.
    [Fact]
    public async Task SyncCardiMemberAsync_KeepsTheRoutineSuccess_WhenGranularFails()
    {
        SetupSuccessfulTokenRefresh();
        SetupDefaultApiResponse();
        _deviceApi.GetGranularDayAsync(Arg.Any<string>(), Arg.Any<DateOnly>())
            .ThrowsAsync(new GoogleHealthApiException(503, "Service Unavailable"));

        await Assert.ThrowsAsync<GoogleHealthApiException>(() =>
            CreateSut().SyncCardiMemberAsync(_fitbitConnection, SyncScope.WorkerCadence));

        await _deviceConnections.Received(1)
            .MarkSyncSucceededAsync(_fitbitConnection.Id, Arg.Any<DateTime>());
        // Granular precedes backfill in the worker-cadence extras, so the chunk never started.
        await _deviceApi.Received(WindowDays(LookbackDays))
            .GetHealthSnapshotAsync(Arg.Any<string>(), Arg.Any<DateOnly>());
    }

    [Fact]
    public async Task SyncCardiMemberAsync_NeverTouchesGranular_AtRoutineScope()
    {
        SetupSuccessfulTokenRefresh();
        SetupDefaultApiResponse();

        await CreateSut().SyncCardiMemberAsync(_fitbitConnection);

        await _deviceApi.DidNotReceive().GetGranularDayAsync(Arg.Any<string>(), Arg.Any<DateOnly>());
    }

    [Fact]
    public async Task SyncCardiMemberAsync_SkipsIngestion_WhenTheGranularDayIsEmpty()
    {
        SetupSuccessfulTokenRefresh();
        SetupDefaultApiResponse();
        _deviceApi.GetGranularDayAsync(Arg.Any<string>(), Arg.Any<DateOnly>())
            .Returns(DeviceGranularDay.Empty);

        await CreateSut().SyncCardiMemberAsync(_fitbitConnection, SyncScope.WorkerCadence);

        await _granularIngestion.DidNotReceive().IngestDayAsync(
            Arg.Any<DeviceConnection>(), Arg.Any<DeviceGranularDay>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncCardiMemberAsync_DoesNotFetchGranular_ForBackfillDays()
    {
        SetupSuccessfulTokenRefresh();
        SetupDefaultApiResponse();
        _deviceApi.GetGranularDayAsync(Arg.Any<string>(), Arg.Any<DateOnly>())
            .Returns(GranularDayWithData());

        await CreateSut().SyncCardiMemberAsync(_fitbitConnection, SyncScope.WorkerCadence);

        // The backfill chunk fetched daily snapshots beyond the routine window…
        await _deviceApi.Received(WindowDays(LookbackDays) + _googleHealthConfig.BackfillChunkDays)
            .GetHealthSnapshotAsync(Arg.Any<string>(), Arg.Any<DateOnly>());
        // …but the granular fetch stayed within it.
        await _deviceApi.Received(WindowDays(LookbackDays))
            .GetGranularDayAsync(Arg.Any<string>(), Arg.Any<DateOnly>());
    }

    [Fact]
    public async Task AuditSyncAsync_NeverTouchesGranular()
    {
        SetupSuccessfulTokenRefresh();
        SetupDefaultApiResponse();

        await CreateSut().AuditSyncAsync(_fitbitConnection);

        await _deviceApi.DidNotReceive().GetGranularDayAsync(Arg.Any<string>(), Arg.Any<DateOnly>());
    }

    // ── Health-user identity capture ─────────────────────────────────────────────
    //
    // The provider's public health-user id maps webhook notifications back to a connection, and
    // the connect flow predates the column — so sync captures it, once, best-effort.

    [Fact]
    public async Task SyncCardiMemberAsync_CapturesTheHealthUserId_WhenMissing()
    {
        SetupSuccessfulTokenRefresh();
        SetupDefaultApiResponse();
        _deviceApi.GetHealthUserIdAsync(Arg.Any<string>()).Returns("abc-123");

        await CreateSut().SyncCardiMemberAsync(_fitbitConnection);

        await _deviceConnections.Received(1).UpdateHealthUserIdAsync(_fitbitConnection.Id, "abc-123");
        Assert.Equal("abc-123", _fitbitConnection.HealthUserId);
    }

    [Fact]
    public async Task SyncCardiMemberAsync_DoesNotAskForTheIdentityAgain_OnceCaptured()
    {
        _fitbitConnection.HealthUserId = "abc-123";
        SetupSuccessfulTokenRefresh();
        SetupDefaultApiResponse();

        await CreateSut().SyncCardiMemberAsync(_fitbitConnection);

        await _deviceApi.DidNotReceive().GetHealthUserIdAsync(Arg.Any<string>());
    }

    // Best-effort by design: an account without an id still syncs; it just cannot be addressed
    // by webhooks yet, and the next pull asks again.
    [Fact]
    public async Task SyncCardiMemberAsync_SyncsNormally_WhenNoIdentityIsAvailable()
    {
        SetupSuccessfulTokenRefresh();
        SetupDefaultApiResponse();
        _deviceApi.GetHealthUserIdAsync(Arg.Any<string>()).Returns((string?)null);

        await CreateSut().SyncCardiMemberAsync(_fitbitConnection);

        await _deviceConnections.DidNotReceive().UpdateHealthUserIdAsync(Arg.Any<Guid>(), Arg.Any<string>());
        await _deviceApi.Received(WindowDays(LookbackDays))
            .GetHealthSnapshotAsync(Arg.Any<string>(), Arg.Any<DateOnly>());
    }

    // ── AuditSyncAsync ───────────────────────────────────────────────────────────
    //
    // The audit exists to measure how far back a provider revises data — something a routine sync
    // structurally cannot see, since it only ever looks inside its own window. That makes it an
    // observation: it must reach further back, and it must leave no trace on the connection.

    [Fact]
    public async Task AuditSyncAsync_FetchesTheWiderAuditWindow()
    {
        _googleHealthConfig.AuditLookbackDays = 14;
        SetupSuccessfulTokenRefresh();
        SetupDefaultApiResponse();

        await CreateSut().AuditSyncAsync(_fitbitConnection);

        await _deviceApi.Received(WindowDays(14)).GetHealthSnapshotAsync(Arg.Any<string>(), Arg.Any<DateOnly>());
        await _deviceActivityLogs.Received(WindowDays(14)).UpsertAsync(Arg.Any<DeviceActivityLog>());
    }

    // Narrower than the routine window would make the audit blinder than the thing it checks.
    [Fact]
    public async Task AuditSyncAsync_FallsBackToTheSyncWindow_WhenAuditWindowIsNarrower()
    {
        _googleHealthConfig.AuditLookbackDays = 1;
        SetupSuccessfulTokenRefresh();
        SetupDefaultApiResponse();

        await CreateSut().AuditSyncAsync(_fitbitConnection);

        await _deviceApi.Received(WindowDays(LookbackDays))
            .GetHealthSnapshotAsync(Arg.Any<string>(), Arg.Any<DateOnly>());
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
            .ThrowsAsync(new GoogleHealthApiException(500, "Internal Server Error"));

        await Assert.ThrowsAsync<GoogleHealthApiException>(() =>
            CreateSut().AuditSyncAsync(_fitbitConnection));

        await _deviceConnections.DidNotReceive()
            .UpdateStatusAsync(Arg.Any<Guid>(), Arg.Any<ConnectionStatus>());
    }

    // Whatever the audit turns up is still merged, so a provider's late correction to a member's
    // history is repaired as a side effect of measuring it.
    [Fact]
    public async Task AuditSyncAsync_RecomputesTheMergedRow_ForEveryDayFetched()
    {
        _googleHealthConfig.AuditLookbackDays = 5;
        SetupSuccessfulTokenRefresh();
        SetupDefaultApiResponse();

        await CreateSut().AuditSyncAsync(_fitbitConnection);

        for (var offset = 0; offset < WindowDays(5); offset++)
        {
            await _aggregation.Received(1)
                .RecomputeAsync(_fitbitConnection.CardiMemberId, Today.AddDays(-offset));
        }
    }

    // ---------------------------------------------------------------- battery capture

    private const string SettingsScope =
        """["activity_and_fitness","health_metrics_and_measurements","sleep","settings"]""";

    private static PairedDeviceInfo Paired(
        int? level, string? status = null, string deviceType = "TRACKER") =>
        new(deviceType, level, status, "Charge 6", DateTime.UtcNow);

    [Fact]
    public async Task SyncCardiMemberAsync_StoresTheBatteryReading()
    {
        SetupSuccessfulTokenRefresh();
        SetupDefaultApiResponse();
        _fitbitConnection.Scopes = SettingsScope;
        _deviceApi.GetPairedDevicesAsync(Arg.Any<string>())
            .Returns([Paired(8, "Low")]);

        await CreateSut().SyncCardiMemberAsync(_fitbitConnection);

        await _deviceConnections.Received(1)
            .UpdateBatteryAsync(_fitbitConnection.Id, 8, "Low", Arg.Any<DateTime>());
    }

    [Fact]
    public async Task SyncCardiMemberAsync_DoesNotCallTheDeviceRegistry_WithoutTheSettingsScope()
    {
        // The common case for every wearer connected before the scope shipped. Asking anyway would
        // buy a guaranteed 403 on every connection, every ten minutes, forever.
        SetupSuccessfulTokenRefresh();
        SetupDefaultApiResponse();
        _fitbitConnection.Scopes = """["activity_and_fitness","sleep"]""";

        await CreateSut().SyncCardiMemberAsync(_fitbitConnection);

        await _deviceApi.DidNotReceive().GetPairedDevicesAsync(Arg.Any<string>());
        await _deviceConnections.DidNotReceive()
            .UpdateBatteryAsync(Arg.Any<Guid>(), Arg.Any<int?>(), Arg.Any<string>(), Arg.Any<DateTime>());
    }

    [Fact]
    public async Task SyncCardiMemberAsync_ReportsTheLowestBattery_AcrossSeveralPairedDevices()
    {
        // A caregiver needs to know something is about to stop reporting; taking the first or the
        // average would hide exactly that.
        SetupSuccessfulTokenRefresh();
        SetupDefaultApiResponse();
        _fitbitConnection.Scopes = SettingsScope;
        _deviceApi.GetPairedDevicesAsync(Arg.Any<string>())
            .Returns([Paired(70, "High"), Paired(6, "Low"), Paired(45, "Medium")]);

        await CreateSut().SyncCardiMemberAsync(_fitbitConnection);

        await _deviceConnections.Received(1)
            .UpdateBatteryAsync(_fitbitConnection.Id, 6, "Low", Arg.Any<DateTime>());
    }

    [Fact]
    public async Task SyncCardiMemberAsync_IgnoresAScalesMissingBattery()
    {
        SetupSuccessfulTokenRefresh();
        SetupDefaultApiResponse();
        _fitbitConnection.Scopes = SettingsScope;
        _deviceApi.GetPairedDevicesAsync(Arg.Any<string>())
            .Returns([Paired(null, null, "SCALE"), Paired(55, "Medium")]);

        await CreateSut().SyncCardiMemberAsync(_fitbitConnection);

        await _deviceConnections.Received(1)
            .UpdateBatteryAsync(_fitbitConnection.Id, 55, "Medium", Arg.Any<DateTime>());
    }

    [Fact]
    public async Task SyncCardiMemberAsync_SucceedsWhenTheBatteryReadFails()
    {
        // Battery is a convenience reading about hardware. Losing it must never cost the member
        // their health data, nor park a working connection in SyncError.
        SetupSuccessfulTokenRefresh();
        SetupDefaultApiResponse();
        _fitbitConnection.Scopes = SettingsScope;
        _deviceApi.GetPairedDevicesAsync(Arg.Any<string>())
            .ThrowsAsync(new GoogleHealthApiException(500, "Internal Server Error"));

        await CreateSut().SyncCardiMemberAsync(_fitbitConnection);

        await _deviceConnections.Received(1)
            .MarkSyncSucceededAsync(_fitbitConnection.Id, Arg.Any<DateTime>());
        await _deviceConnections.DidNotReceive()
            .UpdateStatusAsync(_fitbitConnection.Id, ConnectionStatus.SyncError);
    }

    [Fact]
    public async Task SyncCardiMemberAsync_WritesNoBattery_WhenNoDeviceReportsOne()
    {
        SetupSuccessfulTokenRefresh();
        SetupDefaultApiResponse();
        _fitbitConnection.Scopes = SettingsScope;
        _deviceApi.GetPairedDevicesAsync(Arg.Any<string>())
            .Returns([Paired(null, null)]);

        await CreateSut().SyncCardiMemberAsync(_fitbitConnection);

        await _deviceConnections.DidNotReceive()
            .UpdateBatteryAsync(Arg.Any<Guid>(), Arg.Any<int?>(), Arg.Any<string>(), Arg.Any<DateTime>());
    }

    // ── Battery crossing into or out of "low" re-evaluates the member's gaps ───
    //
    // The cadence half of the low-battery warning. DataCompletenessWorker runs at 06:00 daily, and
    // a device that is minutes-to-hours from stopping cannot wait for it — reconciling on the
    // crossing puts DEVICE_BATTERY_LOW within a sync cycle of the reading that opened it.

    [Fact]
    public async Task SyncCardiMemberAsync_ReevaluatesGaps_WhenTheBatteryDropsIntoLow()
    {
        SetupSuccessfulTokenRefresh();
        SetupDefaultApiResponse();
        _fitbitConnection.Scopes = SettingsScope;
        _fitbitConnection.BatteryLevel = 80;
        _fitbitConnection.BatteryUpdatedAt = DateTime.UtcNow.AddMinutes(-10);
        _deviceApi.GetPairedDevicesAsync(Arg.Any<string>()).Returns([Paired(8, "Low")]);

        await CreateSut().SyncCardiMemberAsync(_fitbitConnection);

        await _gapResolver.Received(1).ResolveForCardiMemberAsync(
            _fitbitConnection.CardiMemberId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncCardiMemberAsync_ReevaluatesGaps_WhenTheBatteryRecoversOutOfLow()
    {
        // The closing half: charged overnight, so the nudge should resolve now rather than sit in
        // the inbox contradicting a device list that already reads 90%.
        SetupSuccessfulTokenRefresh();
        SetupDefaultApiResponse();
        _fitbitConnection.Scopes = SettingsScope;
        _fitbitConnection.BatteryLevel = 8;
        _fitbitConnection.BatteryStatus = "Low";
        _fitbitConnection.BatteryUpdatedAt = DateTime.UtcNow.AddMinutes(-10);
        _deviceApi.GetPairedDevicesAsync(Arg.Any<string>()).Returns([Paired(90, "High")]);

        await CreateSut().SyncCardiMemberAsync(_fitbitConnection);

        await _gapResolver.Received(1).ResolveForCardiMemberAsync(
            _fitbitConnection.CardiMemberId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncCardiMemberAsync_DoesNotReevaluateGaps_WhenTheBatteryStaysLow()
    {
        // Every ten minutes, per connection, for as long as the battery stays flat. Reconciliation
        // loads the member's whole snapshot — spending it to rediscover an unchanged gap is the
        // cost this guard exists to stop.
        SetupSuccessfulTokenRefresh();
        SetupDefaultApiResponse();
        _fitbitConnection.Scopes = SettingsScope;
        _fitbitConnection.BatteryLevel = 9;
        _fitbitConnection.BatteryStatus = "Low";
        _fitbitConnection.BatteryUpdatedAt = DateTime.UtcNow.AddMinutes(-10);
        _deviceApi.GetPairedDevicesAsync(Arg.Any<string>()).Returns([Paired(7, "Low")]);

        await CreateSut().SyncCardiMemberAsync(_fitbitConnection);

        await _gapResolver.DidNotReceive().ResolveForCardiMemberAsync(
            Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncCardiMemberAsync_ReevaluatesGaps_WhenAStaleLowReadingIsReplacedByAFreshOne()
    {
        // A reading past DeviceBattery.FreshFor is not "low" to DeviceBatteryLowRule, which skips
        // it on the same freshness test. Treating the stored value as low here would report no
        // transition and leave the gap unopened — the rule can see it, so this must too.
        SetupSuccessfulTokenRefresh();
        SetupDefaultApiResponse();
        _fitbitConnection.Scopes = SettingsScope;
        _fitbitConnection.BatteryLevel = 9;
        _fitbitConnection.BatteryStatus = "Low";
        _fitbitConnection.BatteryUpdatedAt = DateTime.UtcNow - DeviceBattery.FreshFor.Add(TimeSpan.FromHours(1));
        _deviceApi.GetPairedDevicesAsync(Arg.Any<string>()).Returns([Paired(7, "Low")]);

        await CreateSut().SyncCardiMemberAsync(_fitbitConnection);

        await _gapResolver.Received(1).ResolveForCardiMemberAsync(
            _fitbitConnection.CardiMemberId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncCardiMemberAsync_StillSucceeds_WhenReevaluatingGapsThrows()
    {
        // Same contract as the battery read itself: a notification that cannot be reconciled must
        // never cost the member the health data this sync just landed.
        SetupSuccessfulTokenRefresh();
        SetupDefaultApiResponse();
        _fitbitConnection.Scopes = SettingsScope;
        _fitbitConnection.BatteryLevel = 80;
        _fitbitConnection.BatteryUpdatedAt = DateTime.UtcNow.AddMinutes(-10);
        _deviceApi.GetPairedDevicesAsync(Arg.Any<string>()).Returns([Paired(8, "Low")]);
        _gapResolver.ResolveForCardiMemberAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("reconciliation unavailable"));

        var thrown = await Record.ExceptionAsync(
            () => CreateSut().SyncCardiMemberAsync(_fitbitConnection));

        Assert.Null(thrown);
        await _deviceConnections.Received(1)
            .MarkSyncSucceededAsync(_fitbitConnection.Id, Arg.Any<DateTime>());
    }
}
