using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.ExternalClients;
using CardiTrack.Infrastructure.Settings;
using Microsoft.Extensions.Options;

namespace CardiTrack.Infrastructure.Services;

/// <summary>
/// Generic sync service that works with any IDeviceApiClient implementation.
/// Register one instance per provider using .NET keyed services keyed on DeviceType.
/// </summary>
public class DeviceSyncService : IDeviceSyncService
{
    private readonly IOAuthTokenRefreshService _tokenRefresh;
    private readonly IDeviceApiClient _deviceApi;
    private readonly IDeviceConnectionRepository _deviceConnections;
    private readonly IDeviceActivityLogRepository _deviceActivityLogs;
    private readonly IActivityLogAggregationService _aggregation;
    private readonly IUnitOfWork _unitOfWork;
    private readonly List<DeviceProviderSettings> _providers;

    public DeviceSyncService(
        IOAuthTokenRefreshService tokenRefresh,
        IDeviceApiClient deviceApi,
        IDeviceConnectionRepository deviceConnections,
        IDeviceActivityLogRepository deviceActivityLogs,
        IActivityLogAggregationService aggregation,
        IUnitOfWork unitOfWork,
        IOptions<List<DeviceProviderSettings>> providers)
    {
        _tokenRefresh = tokenRefresh;
        _deviceApi = deviceApi;
        _deviceConnections = deviceConnections;
        _deviceActivityLogs = deviceActivityLogs;
        _aggregation = aggregation;
        _unitOfWork = unitOfWork;
        _providers = providers.Value;
    }

    public async Task SyncCardiMemberAsync(DeviceConnection connection)
    {
        var providerConfig = ResolveProviderConfig(connection);

        // RefreshIfExpiredAsync marks the connection itself when the provider refuses the grant;
        // letting the failure propagate is what gets it logged against this connection by the
        // caller.
        var accessToken = await _tokenRefresh.RefreshIfExpiredAsync(connection, providerConfig);

        var lookbackDays = Math.Max(1, providerConfig.SyncLookbackDays);

        try
        {
            await PullWindowAsync(connection, accessToken, lookbackDays);

            // Only once the whole window landed — otherwise a partial sync would look complete
            // and the connection would not come due again until the next interval. This also
            // clears a SyncError left by an earlier run: the window just landed, so whatever
            // the provider was doing then, the connection is working now.
            await _deviceConnections.MarkSyncSucceededAsync(connection.Id, DateTime.UtcNow);
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
        await PullWindowAsync(connection, accessToken, lookbackDays);
    }

    private DeviceProviderSettings ResolveProviderConfig(DeviceConnection connection) =>
        _providers.FirstOrDefault(p => p.Provider.Equals(
            connection.DeviceType.ToString(), StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException(
            $"No provider config found for device type '{connection.DeviceType}'.");

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
    /// trailing days, which reach back <paramref name="lookbackDays"/> complete days — unchanged,
    /// so a day missed while the puller was down is still picked up on a later run rather than
    /// being lost for good.
    /// </para>
    /// </remarks>
    private async Task PullWindowAsync(DeviceConnection connection, string accessToken, int lookbackDays)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        // Oldest first, so a mid-window provider failure still leaves the earlier days stored.
        for (var offset = lookbackDays; offset >= 0; offset--)
        {
            var targetDate = today.AddDays(-offset);
            var snapshot = await _deviceApi.GetHealthSnapshotAsync(accessToken, targetDate);

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
                Temperature = snapshot.Temperature
            };

            // Save the raw row first — the merge reads every device's stored row for the day,
            // so it has to see this one.
            await _deviceActivityLogs.UpsertAsync(log);
            await _unitOfWork.SaveChangesAsync();

            await _aggregation.RecomputeAsync(connection.CardiMemberId, targetDate);
            await _unitOfWork.SaveChangesAsync();
        }
    }

    /// <summary>
    /// Returns true for exceptions that represent a provider API failure (as opposed to
    /// infrastructure failures like network timeouts). Override in provider-specific subclasses
    /// if needed, or broaden to catch a common base exception type.
    /// </summary>
    protected virtual bool IsProviderApiException(Exception ex) =>
        ex is FitbitApiException;
}
