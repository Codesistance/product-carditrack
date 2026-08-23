using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services;
using CardiTrack.Application.Services.Notifications;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.ExternalClients;
using CardiTrack.Infrastructure.Settings;
using CardiTrack.Shared.Json;
using Microsoft.Extensions.Options;

namespace CardiTrack.Infrastructure.Services;

/// <summary>
/// Generic sync service that works with any IDeviceApiClient implementation.
/// Register one instance per data-source API using .NET keyed services keyed on HealthApi;
/// a connection reaches its engine through the configured DeviceType→HealthApi mapping.
/// </summary>
public class DeviceSyncService : IDeviceSyncService
{
    private readonly IOAuthTokenRefreshService _tokenRefresh;
    private readonly IDeviceApiClient _deviceApi;
    private readonly IDeviceConnectionRepository _deviceConnections;
    private readonly IDeviceActivityLogRepository _deviceActivityLogs;
    private readonly IActivityLogAggregationService _aggregation;
    private readonly IGranularIngestionService _granularIngestion;
    private readonly IUnitOfWork _unitOfWork;
    private readonly INotificationGapResolver _gapResolver;
    private readonly List<DeviceProviderSettings> _providers;

    public DeviceSyncService(
        IOAuthTokenRefreshService tokenRefresh,
        IDeviceApiClient deviceApi,
        IDeviceConnectionRepository deviceConnections,
        IDeviceActivityLogRepository deviceActivityLogs,
        IActivityLogAggregationService aggregation,
        IGranularIngestionService granularIngestion,
        IUnitOfWork unitOfWork,
        INotificationGapResolver gapResolver,
        IOptions<List<DeviceProviderSettings>> providers)
    {
        _tokenRefresh = tokenRefresh;
        _deviceApi = deviceApi;
        _deviceConnections = deviceConnections;
        _deviceActivityLogs = deviceActivityLogs;
        _aggregation = aggregation;
        _granularIngestion = granularIngestion;
        _unitOfWork = unitOfWork;
        _gapResolver = gapResolver;
        _providers = providers.Value;
    }

    public async Task SyncCardiMemberAsync(DeviceConnection connection, SyncScope scope = SyncScope.Routine)
    {
        var providerConfig = ResolveProviderConfig(connection);

        // RefreshIfExpiredAsync marks the connection itself when the provider refuses the grant;
        // letting the failure propagate is what gets it logged against this connection by the
        // caller.
        var accessToken = await _tokenRefresh.RefreshIfExpiredAsync(connection, providerConfig);

        // Read once and passed down, rather than each step asking the clock again. The token
        // refresh above is network I/O, so a sync that starts just before UTC midnight can reach
        // the pull on the following day: two independent reads would then decide "no repair pass
        // needed" against the old day and fetch against the new one, skipping that day's backfill
        // entirely — and the next pull, now stamped with the new day, would not make it up.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        // The trailing repair days are re-fetched once a UTC day, not on every pull. They exist to
        // catch a provider revising a *finished* day, which happens on the order of hours — paying
        // for them every ten minutes would spend the whole per-user quota re-reading numbers that
        // cannot have moved. Today is pulled every time, which is the part a caregiver sees.
        var needsRepairPass = connection.LastSyncDate is not { } last
            || DateOnly.FromDateTime(last) != today;
        var lookbackDays = needsRepairPass ? Math.Max(1, providerConfig.SyncLookbackDays) : 0;

        try
        {
            // One-time identity capture: the provider's public health-user id is what maps a
            // webhook notification back to this connection, and the OAuth connect flow predates
            // the column — capturing here self-heals every existing connection on its next pull.
            // Best-effort by design: a wearer without one still syncs, they just cannot be
            // addressed by webhooks until the provider exposes it.
            if (connection.HealthUserId is null)
            {
                var healthUserId = await _deviceApi.GetHealthUserIdAsync(accessToken);
                if (healthUserId is not null)
                {
                    await _deviceConnections.UpdateHealthUserIdAsync(connection.Id, healthUserId);
                    connection.HealthUserId = healthUserId;
                }
            }

            await CaptureBatteryAsync(connection, accessToken);

            await PullWindowAsync(connection, accessToken, lookbackDays, today);

            // Only once the whole window landed — otherwise a partial sync would look complete
            // and the connection would not come due again until the next interval. This also
            // clears a SyncError left by an earlier run: the window just landed, so whatever
            // the provider was doing then, the connection is working now.
            await _deviceConnections.MarkSyncSucceededAsync(connection.Id, DateTime.UtcNow);

            // The worker-cadence extras run after the routine window succeeded, never inside its
            // success envelope: both are enrichment, and a transient failure in either must not
            // un-succeed the daily data that already landed — that would re-fetch the whole
            // window next pull and could park a working connection in SyncError over a series
            // the caregiver never sees directly. Scoped to the worker cadence because the
            // manual-sync path shares this method, and a caregiver waiting on a refresh must not
            // pay for a chunk of last month or four extra series.
            if (scope == SyncScope.WorkerCadence)
            {
                await IngestGranularWindowAsync(connection, accessToken, lookbackDays, today);
                await BackfillHistoryAsync(connection, accessToken, providerConfig, today);
            }
        }
        catch (Exception ex) when (IsProviderApiException(ex))
        {
            await _deviceConnections.UpdateStatusAsync(connection.Id, ConnectionStatus.SyncError);
            throw;
        }
    }

    public async Task AuditSyncAsync(DeviceConnection connection)
    {
        var providerConfig = ResolveProviderConfig(connection);
        var accessToken = await _tokenRefresh.RefreshIfExpiredAsync(connection, providerConfig);

        // Never narrower than the routine window, or the audit could see less than the thing it
        // exists to check.
        var lookbackDays = Math.Max(
            Math.Max(1, providerConfig.SyncLookbackDays), providerConfig.AuditLookbackDays);

        // No LastSyncDate stamp and no SyncError transition: see IDeviceSyncService.AuditSyncAsync.
        // Any revision this turns up still lands in the raw row and is merged, so the audit
        // repairs history as a side effect of measuring it.
        await PullWindowAsync(connection, accessToken, lookbackDays, DateOnly.FromDateTime(DateTime.UtcNow));
    }

    /// <summary>
    /// Reads the wearable's battery from the provider's device registry and stores the last-known
    /// value on the connection. One request, on every pull including a caregiver's manual refresh:
    /// the whole point of the reading is that it is current when someone looks at it, and unlike
    /// the granular series or the history backfill it does not scale with the window.
    /// </summary>
    /// <remarks>
    /// Best-effort in the strongest sense — this method never throws. Battery is a convenience
    /// reading about hardware, and no failure to obtain it may cost the member their health data
    /// or park a working connection in <see cref="ConnectionStatus.SyncError"/>. It sits before
    /// <see cref="PullWindowAsync"/> only so a caregiver opening the device list mid-sync sees a
    /// fresh battery alongside a window still landing; the ordering carries no dependency.
    /// <para>
    /// Skipped outright when the connection never granted the settings scope, so the common case
    /// for a pre-existing wearer costs no request at all rather than a guaranteed 403 every ten
    /// minutes. The client tolerates that 403 anyway, for the window where this stored scope list
    /// and the token's real grant disagree.
    /// </para>
    /// </remarks>
    private async Task CaptureBatteryAsync(DeviceConnection connection, string accessToken)
    {
        if (!DeviceScopes.GrantsSettings(ParseScopes(connection.Scopes)))
            return;

        IReadOnlyList<PairedDeviceInfo> pairedDevices;
        try
        {
            pairedDevices = await _deviceApi.GetPairedDevicesAsync(accessToken);
        }
        catch (Exception ex) when (IsProviderApiException(ex))
        {
            return;
        }

        // The provider reports every wearable on the account, and CardiTrack's connection does not
        // name which one it is. The lowest battery among the battery-powered ones is the reading
        // that matters: a caregiver needs to know something is about to stop reporting, and
        // averaging or taking the first would hide exactly that.
        var lowest = pairedDevices
            .Where(d => d.IsBatteryPowered && (d.BatteryLevel is not null || d.BatteryStatus is not null))
            .OrderBy(d => d.BatteryLevel ?? int.MaxValue)
            .FirstOrDefault();

        if (lowest is null)
            return;

        var readAt = DateTime.UtcNow;

        // Whether the *stored* reading counted as low before this one landed. A stale reading is
        // not low for this purpose no matter what number it holds — DeviceBatteryLowRule ignores
        // it on exactly the same freshness test, so treating it as low here would report a
        // transition the rule cannot see.
        var wasLow = DeviceBattery.IsFresh(connection.BatteryUpdatedAt, readAt)
                     && DeviceBattery.IsLow(connection.BatteryLevel, connection.BatteryStatus);
        var isLow = DeviceBattery.IsLow(lowest.BatteryLevel, lowest.BatteryStatus);

        await _deviceConnections.UpdateBatteryAsync(
            connection.Id, lowest.BatteryLevel, lowest.BatteryStatus, readAt);

        connection.BatteryLevel = lowest.BatteryLevel;
        connection.BatteryStatus = lowest.BatteryStatus;
        connection.BatteryUpdatedAt = readAt;

        if (wasLow != isLow)
            await ReevaluateGapsAsync(connection.CardiMemberId);
    }

    /// <summary>
    /// Re-runs gap detection for the member, so a battery that has just crossed into (or back out
    /// of) "low" opens or closes <c>DEVICE_BATTERY_LOW</c> now rather than at the next
    /// <c>DataCompletenessWorker</c> run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Cadence is the whole point. The daily 06:00 sweep is the wrong clock for a device the rule
    /// itself describes as minutes-to-hours from stopping — a warning that arrives up to 24 hours
    /// after the reading has already been overtaken by the thing it was warning about. Reconciling
    /// here puts it within a sync cycle. The push follows from
    /// <c>NotificationDispatchWorker</c>'s sweep; nothing about delivery is decided in this class.
    /// </para>
    /// <para>
    /// Only on a <em>transition</em>, never on every pull. Connections sync every ten minutes and
    /// reconciliation loads a member's whole snapshot — running it each time would re-evaluate the
    /// estate roughly 144 times a day per connection to discover nothing changed. The gap opens and
    /// closes on the crossing, so the crossing is the only moment worth spending it on.
    /// </para>
    /// <para>
    /// Best-effort, like everything else in <see cref="CaptureBatteryAsync"/>: a caller's health
    /// data must not be lost because a notification could not be reconciled. A missed call here
    /// delays the nudge to the daily run — which is exactly where it stood before this existed.
    /// </para>
    /// </remarks>
    private async Task ReevaluateGapsAsync(Guid cardiMemberId)
    {
        try
        {
            await _gapResolver.ResolveForCardiMemberAsync(cardiMemberId);
        }
        catch (Exception)
        {
            // Deliberately swallowed — see the remarks. DataCompletenessWorker remains the backstop.
        }
    }

    /// <summary>Scopes are stored as a JSON array; a malformed value must not break a sync.</summary>
    private static List<string> ParseScopes(string? scopes) =>
        JsonUtility.TryDeserialize<List<string>>(scopes ?? "[]", out var parsed, out _) && parsed is not null
            ? parsed
            : [];

    private DeviceProviderSettings ResolveProviderConfig(DeviceConnection connection) =>
        _providers.ConfigFor(connection.DeviceType)
        ?? throw new InvalidOperationException(
            $"No provider config claims device type '{connection.DeviceType}' in its DeviceTypes.");

    /// <summary>
    /// Fetches a trailing window ending at today, storing each day and re-merging it.
    /// </summary>
    /// <remarks>
    /// The window ends at <em>today</em> so the dashboard's Key Metrics move during the day.
    /// Ending it at yesterday — which is what this did until now — meant the merged
    /// <c>ActivityLogs</c> row for today never existed, so every reader of "the latest day" was
    /// serving a completed day no matter how often the caregiver pulled to refresh.
    /// <para>
    /// Today's numbers are necessarily partial, and that is the caller's problem to know about,
    /// not a reason to withhold them: <c>DashboardService</c> suppresses the
    /// compare-against-baseline reading for a day still in progress, and baselines are calculated
    /// over completed days only. What providers finalise after midnight is instead covered by the
    /// trailing days, which reach back <paramref name="lookbackDays"/> complete days, so a day
    /// missed while the puller was down is still picked up on a later run rather than being lost
    /// for good.
    /// </para>
    /// <para>
    /// <paramref name="lookbackDays"/> of 0 fetches today alone. That is the routine case: callers
    /// pass the trailing days only on the first pull of a UTC day, because re-reading finished days
    /// every ten minutes costs a per-user quota that today's numbers have a better claim on.
    /// </para>
    /// </remarks>
    private async Task PullWindowAsync(
        DeviceConnection connection, string accessToken, int lookbackDays, DateOnly today)
    {
        // Oldest first, so a mid-window provider failure still leaves the earlier days stored.
        for (var offset = lookbackDays; offset >= 0; offset--)
        {
            var targetDate = today.AddDays(-offset);
            var snapshot = await _deviceApi.GetHealthSnapshotAsync(accessToken, targetDate);
            await StoreDayAsync(connection, snapshot, targetDate);
        }
    }

    /// <summary>
    /// Fetches and stores the granular (minute-grain) series for the routine window's days.
    /// Runs only after the whole daily window landed and was marked successful, so an hour
    /// vector never exists without its daily parent and a granular failure never un-succeeds
    /// the sync a caregiver depends on. Backfill days deliberately get no granular pass: how
    /// far back the provider serves intraday history is unverified (granular ADR open
    /// question), so history stays daily-grain until the probe answers it.
    /// </summary>
    private async Task IngestGranularWindowAsync(
        DeviceConnection connection, string accessToken, int lookbackDays, DateOnly today)
    {
        for (var offset = lookbackDays; offset >= 0; offset--)
        {
            var targetDate = today.AddDays(-offset);
            var granularDay = await _deviceApi.GetGranularDayAsync(accessToken, targetDate);
            if (granularDay is { HasAnyData: true })
                await _granularIngestion.IngestDayAsync(connection, granularDay);
        }
    }

    /// <summary>
    /// Extends a connection's stored history backwards, one chunk per pull, until
    /// <see cref="DeviceProviderSettings.BackfillDays"/> days back from today have been checked.
    /// </summary>
    /// <remarks>
    /// A fresh connection starts with only the routine window, which leaves the 30-day baseline
    /// unreachable for weeks even when the wearable holds months of history the provider would
    /// serve today. Fetching it in one pull is not an option either: a day's snapshot costs 13
    /// requests against the 300/min per-wearer ceiling, so a 90-day one-shot would rate-limit
    /// partway, fail the sync, and start over from scratch on the next pull — burning quota
    /// without ever completing. Each pull therefore takes one chunk, newest-first (the days the
    /// 30-day baseline needs soonest), and <see cref="DeviceConnection.HistoryBackfilledTo"/>
    /// advances per day so an interrupted chunk resumes where it stopped.
    /// <para>
    /// Days the provider has nothing for are checked but not stored: an all-null row would read
    /// as a "data day" to <c>BaselineCalculator</c>'s coverage gate and the dashboard's
    /// days-captured figure, letting a mostly-empty history fake its way past both. The marker
    /// still advances — checked-and-empty is a final answer, not a retry.
    /// </para>
    /// </remarks>
    private async Task BackfillHistoryAsync(
        DeviceConnection connection, string accessToken, DeviceProviderSettings providerConfig, DateOnly today)
    {
        if (providerConfig.BackfillDays <= 0)
            return;

        var horizon = today.AddDays(-providerConfig.BackfillDays);

        // Connections that predate the marker resume from the routine window's floor. That
        // re-fetches at most the trailing repair days once, which is cheaper than querying for
        // the true edge of what is already stored.
        var frontier = connection.HistoryBackfilledTo
            ?? today.AddDays(-Math.Max(1, providerConfig.SyncLookbackDays));

        var chunkFloor = frontier.AddDays(-Math.Max(1, providerConfig.BackfillChunkDays));
        for (var date = frontier.AddDays(-1); date >= horizon && date >= chunkFloor; date = date.AddDays(-1))
        {
            var snapshot = await _deviceApi.GetHealthSnapshotAsync(accessToken, date);
            if (snapshot.HasAnyData)
                await StoreDayAsync(connection, snapshot, date);

            await _deviceConnections.UpdateHistoryBackfilledToAsync(connection.Id, date);
            connection.HistoryBackfilledTo = date;
        }
    }

    /// <summary>
    /// Stores one day's snapshot as this device's raw row and re-merges the member's day.
    /// </summary>
    private async Task StoreDayAsync(
        DeviceConnection connection, DeviceHealthSnapshot snapshot, DateOnly targetDate)
    {
        var log = new DeviceActivityLog
        {
            Id = Guid.NewGuid(),
            CardiMemberId = connection.CardiMemberId,
            DeviceConnectionId = connection.Id,
            DataSource = connection.DeviceType,
            Date = targetDate,

            // Activity
            Steps = snapshot.Steps,
            Distance = snapshot.DistanceKm,
            ActiveMinutes = snapshot.ActiveMinutes,
            SedentaryMinutes = snapshot.SedentaryMinutes,
            Floors = snapshot.Floors,
            CaloriesBurned = snapshot.CaloriesBurned,

            // Heart rate
            RestingHeartRate = snapshot.RestingHeartRate,
            AvgHeartRate = snapshot.AvgHeartRate,
            MaxHeartRate = snapshot.MaxHeartRate,
            MinHeartRate = snapshot.MinHeartRate,

            // Sleep
            SleepMinutes = snapshot.TotalSleepMinutes,
            SleepEfficiency = snapshot.SleepEfficiency,
            SleepStartTime = snapshot.SleepStartTime,
            SleepEndTime = snapshot.SleepEndTime,
            DeepSleepMinutes = snapshot.DeepSleepMinutes,
            LightSleepMinutes = snapshot.LightSleepMinutes,
            RemSleepMinutes = snapshot.RemSleepMinutes,
            AwakeMinutes = snapshot.AwakeMinutes,

            // Additional health metrics — null until a provider populates them
            SpO2Average = snapshot.SpO2Average,
            SpO2Min = snapshot.SpO2Min,
            SpO2Max = snapshot.SpO2Max,
            VO2Max = snapshot.VO2Max,
            StressScore = snapshot.StressScore,
            BreathingRate = snapshot.BreathingRate,
            Temperature = snapshot.Temperature,
            TemperatureBaseline = snapshot.TemperatureBaseline,
            TemperatureVariation = snapshot.TemperatureVariation,

            // Overnight readings
            HeartRateVariabilityMs = snapshot.HeartRateVariabilityMs,
            OvernightBreathingRate = snapshot.OvernightBreathingRate,

            // Effort and rest
            LightZoneMinutes = snapshot.LightZoneMinutes,
            ModerateZoneMinutes = snapshot.ModerateZoneMinutes,
            VigorousZoneMinutes = snapshot.VigorousZoneMinutes,
            PeakZoneMinutes = snapshot.PeakZoneMinutes,
            ModerateZoneFloorBpm = snapshot.ModerateZoneFloorBpm,
            LongestSedentaryStretchMinutes = snapshot.LongestSedentaryStretchMinutes,
            LongestSedentaryStretchStartUtc = snapshot.LongestSedentaryStretchStartUtc
        };

        // Save the raw row first — the merge reads every device's stored row for the day,
        // so it has to see this one.
        await _deviceActivityLogs.UpsertAsync(log);
        await _unitOfWork.SaveChangesAsync();

        await _aggregation.RecomputeAsync(connection.CardiMemberId, targetDate);
        await _unitOfWork.SaveChangesAsync();
    }

    /// <summary>
    /// Returns true for exceptions that represent a provider API failure (as opposed to
    /// infrastructure failures like network timeouts). Override in provider-specific subclasses
    /// if needed, or broaden to catch a common base exception type.
    /// </summary>
    protected virtual bool IsProviderApiException(Exception ex) =>
        ex is GoogleHealthApiException;
}
