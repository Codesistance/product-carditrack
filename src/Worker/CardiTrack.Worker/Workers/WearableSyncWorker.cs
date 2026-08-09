using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
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
        : base(options.Get(nameof(WearableSyncWorker)).CronExpression)
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

        foreach (var connection in connections)
        {
            var syncService = scope.ServiceProvider.GetKeyedService<IDeviceSyncService>(connection.DeviceType);

            if (syncService is null)
            {
                _logger.LogWarning(
                    "No sync service registered for DeviceType {DeviceType}. " +
                    "Skipping DeviceConnection {Id}.",
                    connection.DeviceType, connection.Id);
                continue;
            }

            try
            {
                await syncService.SyncCardiMemberAsync(connection, extendHistory: true);
                successCount++;
                _logger.LogInformation(
                    "Synced DeviceConnection {Id} (DeviceType={DeviceType}) for CardiMember {CardiMemberId}.",
                    connection.Id, connection.DeviceType, connection.CardiMemberId);
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
            "WearableSync complete. Success: {Success}, Failed: {Failed}.",
            successCount, failureCount);
    }
}
