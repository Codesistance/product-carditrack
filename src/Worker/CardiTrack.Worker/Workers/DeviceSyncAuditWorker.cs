using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Domain.Entities;
using Microsoft.Extensions.Options;

namespace CardiTrack.Worker.Workers;

/// <summary>
/// Re-fetches a small random sample of connections over a deliberately wide window, so we can see
/// how far back each provider still revises data.
/// </summary>
/// <remarks>
/// <para>
/// The routine sync can only ever observe revisions inside its own trailing window: with a 3-day
/// lookback, a provider that amends day 5 is not merely unmeasured, it is unmeasurable — the
/// resulting picture of "how far back data changes" would be an artefact of our own schedule
/// rather than a fact about the provider. This job is the only thing that looks further out.
/// </para>
/// <para>
/// It is an observation, so it stamps no LastSyncDate and never marks a connection SyncError. A
/// failed token refresh is the one status change it can still cause, and deliberately so: that is
/// a broken connection, not a casualty of the wide window. It does still store and merge whatever
/// it finds, which means a provider's late correction to a member's history gets repaired as a
/// side effect of measuring it.
/// </para>
/// </remarks>
public class DeviceSyncAuditWorker : CronBackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<DeviceSyncAuditOptions> _auditOptions;
    private readonly ILogger<DeviceSyncAuditWorker> _logger;

    public DeviceSyncAuditWorker(
        IOptionsMonitor<WorkerOptions> options,
        IOptionsMonitor<DeviceSyncAuditOptions> auditOptions,
        IServiceScopeFactory scopeFactory,
        ILogger<DeviceSyncAuditWorker> logger)
        : base(options.Get(nameof(DeviceSyncAuditWorker)).CronExpression, logger)
    {
        _scopeFactory = scopeFactory;
        _auditOptions = auditOptions;
        _logger = logger;
    }

    protected override async Task ExecuteJobAsync(CancellationToken stoppingToken)
    {
        var sampleSize = _auditOptions.CurrentValue.SampleSize;

        _logger.LogInformation(
            "DeviceSyncAudit triggered at {Time} for a sample of {SampleSize}.",
            DateTime.UtcNow, sampleSize);

        if (sampleSize <= 0)
        {
            _logger.LogInformation("DeviceSyncAudit skipped — sample size is not positive.");
            return;
        }

        List<DeviceConnection> sample;
        using (var scope = _scopeFactory.CreateScope())
        {
            var deviceConnections = scope.ServiceProvider.GetRequiredService<IDeviceConnectionRepository>();
            sample = (await deviceConnections.GetRandomSyncableSampleAsync(sampleSize)).ToList();
        }

        var successCount = 0;
        var failureCount = 0;

        foreach (var connection in sample)
        {
            if (stoppingToken.IsCancellationRequested)
                break;

            // One scope per connection: the wide window tracks far more entities than a routine
            // sync, and a connection that fails must not take the rest of the sample with it.
            using var scope = _scopeFactory.CreateScope();
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
                await syncService.AuditSyncAsync(connection);
                successCount++;
            }
            catch (Exception ex)
            {
                failureCount++;

                // Warning, not Error: an audit failure costs measurement precision, not data. The
                // connection keeps its status and its normal schedule either way.
                _logger.LogWarning(ex,
                    "Audit pull failed for DeviceConnection {Id} (DeviceType={DeviceType}).",
                    connection.Id, connection.DeviceType);
            }
        }

        _logger.LogInformation(
            "DeviceSyncAudit complete. Sampled: {Sampled}, audited: {Success}, failed: {Failed}.",
            sample.Count, successCount, failureCount);
    }
}
