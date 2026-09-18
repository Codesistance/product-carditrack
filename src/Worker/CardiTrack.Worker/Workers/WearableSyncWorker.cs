using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Infrastructure.Extensions;
using CardiTrack.Infrastructure.ExternalClients;
using Microsoft.Extensions.Options;

namespace CardiTrack.Worker.Workers;

public class WearableSyncWorker : CronBackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<WearableSyncWorker> _logger;

    public WearableSyncWorker(
        IOptionsMonitor<WorkerOptions> options,
        IServiceScopeFactory scopeFactory,
        ILogger<WearableSyncWorker> logger)
        : base(options.Get(nameof(WearableSyncWorker)).CronExpression, logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteJobAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("WearableSync triggered at {Time}", DateTime.UtcNow);

        using var scope = _scopeFactory.CreateScope();
        var deviceConnections = scope.ServiceProvider.GetRequiredService<IDeviceConnectionRepository>();
        // Every due connection syncs. Each writes its own raw DeviceActivityLogs row, and the
        // merge folds a member's devices into the single ActivityLogs row readers consume, so
        // two devices on one member no longer contend for it.
        var connections = await deviceConnections.GetDueForSyncAsync();

        var successCount = 0;
        var failureCount = 0;
        var awaitingReconnectCount = 0;

        foreach (var connection in connections)
        {
            // Engines are keyed by API, not brand: the configured DeviceTypes list decides which
            // API a connection's hardware syncs through.
            var syncService = scope.ServiceProvider.GetDeviceSyncService(connection.DeviceType);

            if (syncService is null)
            {
                _logger.LogWarning(
                    "No provider config or sync service for DeviceType {DeviceType}. " +
                    "Skipping DeviceConnection {Id}.",
                    connection.DeviceType, connection.Id);
                continue;
            }

            try
            {
                await syncService.SyncCardiMemberAsync(connection, SyncScope.WorkerCadence);
                successCount++;
                _logger.LogInformation(
                    "Synced DeviceConnection {Id} (DeviceType={DeviceType}) for CardiMember {CardiMemberId}.",
                    connection.Id, connection.DeviceType, connection.CardiMemberId);
            }
            catch (DeviceGrantRejectedException ex)
            {
                // Nothing here failed. The provider has refused the refresh token, the connection
                // is already retired to TokenExpired, and DeviceAuthRecoveryWorker keeps probing it
                // on a widening backoff while the DEVICE_AUTH_BROKEN nudge asks the caregiver to
                // reconnect — the one thing that can fix it. Logged at Warning and without the
                // stack, and counted apart from the failures, because a wearer withdrawing consent
                // is not this worker breaking: at Error it raised the service's error rate every
                // quarter hour for as long as the device stayed unreconnected.
                awaitingReconnectCount++;
                _logger.LogWarning(
                    "DeviceConnection {Id} (DeviceType={DeviceType}) for CardiMember {CardiMemberId} is "
                    + "awaiting reconnection: the provider refused the refresh token ({StatusCode}).",
                    connection.Id, connection.DeviceType, connection.CardiMemberId, (int)ex.StatusCode);
            }
            catch (Exception ex)
            {
                failureCount++;
                _logger.LogError(ex,
                    "Failed to sync DeviceConnection {Id} (DeviceType={DeviceType}) for CardiMember {CardiMemberId}.",
                    connection.Id, connection.DeviceType, connection.CardiMemberId);
            }
        }

        _logger.LogInformation(
            "WearableSync complete. Success: {Success}, Failed: {Failed}, Awaiting reconnect: {AwaitingReconnect}.",
            successCount, failureCount, awaitingReconnectCount);
    }
}
