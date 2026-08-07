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
        var due = (await deviceConnections.GetDueForSyncAsync()).ToList();

        // ActivityLogs holds one row per CardiMember per day, so only one of a member's devices
        // may write it — otherwise two connections would overwrite each other every cycle. The
        // member's primary device wins, falling back to the longest-connected one so the choice
        // stays stable across runs when no device is flagged primary.
        var connections = due
            .GroupBy(c => c.CardiMemberId)
            .Select(g => g
                .OrderByDescending(c => c.IsPrimary)
                .ThenBy(c => c.ConnectedDate ?? DateTime.MaxValue)
                .ThenBy(c => c.Id)
                .First())
            .ToList();

        var skippedCount = due.Count - connections.Count;
        if (skippedCount > 0)
        {
            _logger.LogInformation(
                "Skipping {Skipped} non-primary connection(s); their CardiMember is synced from another device.",
                skippedCount);
        }

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
                await syncService.SyncCardiMemberAsync(connection);
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
