using CardiTrack.Application.Interfaces.Services;
using Microsoft.Extensions.Options;

namespace CardiTrack.Worker.Workers;

/// <summary>
/// Keeps the partitioned time-series tables alive: pre-creates range partitions ahead of the data
/// and drops the ones wholly past retention (PostgreSQL has neither TTL nor on-demand partition
/// creation). Hourly rather than daily, deliberately — creation is idempotent and near-free, and
/// the frequent cadence is what closes the window between a fresh deploy and the first partition
/// existing, without teaching <see cref="CronBackgroundService"/> a run-on-startup mode.
/// </summary>
public class PartitionMaintenanceWorker : CronBackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<PartitionMaintenanceOptions> _options;
    private readonly ILogger<PartitionMaintenanceWorker> _logger;

    public PartitionMaintenanceWorker(
        IOptionsMonitor<WorkerOptions> workerOptions,
        IOptionsMonitor<PartitionMaintenanceOptions> options,
        IServiceScopeFactory scopeFactory,
        ILogger<PartitionMaintenanceWorker> logger)
        : base(workerOptions.Get(nameof(PartitionMaintenanceWorker)).CronExpression)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteJobAsync(CancellationToken stoppingToken)
    {
        var options = _options.CurrentValue;

        // A bad retention config must not fault the hosted-service loop (an exception escaping
        // ExecuteJobAsync would), and it especially must not reach the drop path — the service
        // also rejects it, but by then the worker would be crash-looping. Log and sit the run out.
        if (options.GranularRetentionDays <= 0 || options.RollupRetentionMonths <= 0
            || options.DigestRetentionMonths <= 0 || options.DaysAhead < 0)
        {
            _logger.LogError(
                "PartitionMaintenance skipped: invalid configuration (DaysAhead={DaysAhead}, " +
                "GranularRetentionDays={GranularDays}, RollupRetentionMonths={RollupMonths}, " +
                "DigestRetentionMonths={DigestMonths}). Retention values must be positive.",
                options.DaysAhead, options.GranularRetentionDays, options.RollupRetentionMonths,
                options.DigestRetentionMonths);
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var partitions = scope.ServiceProvider.GetRequiredService<ITimeSeriesPartitionService>();

        await partitions.EnsureUpcomingPartitionsAsync(options.DaysAhead, stoppingToken);
        await partitions.DropExpiredPartitionsAsync(
            options.GranularRetentionDays, options.RollupRetentionMonths,
            options.DigestRetentionMonths, stoppingToken);

        _logger.LogInformation(
            "PartitionMaintenance complete. Ahead: {DaysAhead} days; retention: granular {GranularDays} days, " +
            "rollups {RollupMonths} months, digests {DigestMonths} months.",
            options.DaysAhead, options.GranularRetentionDays, options.RollupRetentionMonths,
            options.DigestRetentionMonths);
    }
}
