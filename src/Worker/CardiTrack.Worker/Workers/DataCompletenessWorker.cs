using System.Diagnostics;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Services.Notifications;
using CardiTrack.Domain.Entities;
using CardiTrack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CardiTrack.Worker.Workers;

/// <summary>
/// Detects the gaps between what CardiTrack needs and what each account has supplied, and
/// reconciles them against what the caregiver has already been told.
/// </summary>
/// <remarks>
/// <para>
/// Runs after <see cref="BaselineCalculationWorker"/>, so anything it says about learning progress
/// reflects that morning's numbers rather than yesterday's.
/// </para>
/// <para>
/// Reconciliation is idempotent by fingerprint, so a crashed run simply repeats. What the advisory
/// lock buys is not correctness but restraint: at three Cloud Run instances this would otherwise
/// evaluate the whole estate three times over for one useful result.
/// </para>
/// </remarks>
public class DataCompletenessWorker : CronBackgroundService
{
    /// <summary>
    /// Arbitrary but fixed — the key identifies this job across instances. Postgres advisory locks
    /// are namespaced only by the number, so it must not collide with another job's.
    /// </summary>
    private const long AdvisoryLockKey = 8_472_100_001;

    /// <summary>Organizations per batch. Small enough that a cancellation lands promptly.</summary>
    private const int BatchSize = 50;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DataCompletenessWorker> _logger;

    public DataCompletenessWorker(
        IOptionsMonitor<WorkerOptions> options,
        IServiceScopeFactory scopeFactory,
        ILogger<DataCompletenessWorker> logger)
        : base(options.Get(nameof(DataCompletenessWorker)).CronExpression, logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteJobAsync(CancellationToken stoppingToken)
    {
        using var lockScope = _scopeFactory.CreateScope();
        var lockContext = lockScope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();

        // Session-scoped and non-blocking: a second instance that cannot take the lock skips the
        // run entirely rather than queueing behind it and doing the work again afterwards.
        var connection = lockContext.Database.GetDbConnection();
        await connection.OpenAsync(stoppingToken);

        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT pg_try_advisory_lock({AdvisoryLockKey});";
        var acquired = (bool?)await command.ExecuteScalarAsync(stoppingToken) ?? false;

        if (!acquired)
        {
            _logger.LogInformation(
                "DataCompleteness skipped — another instance holds the advisory lock.");
            return;
        }

        try
        {
            await RunAsync(stoppingToken);
        }
        finally
        {
            await using var unlock = connection.CreateCommand();
            unlock.CommandText = $"SELECT pg_advisory_unlock({AdvisoryLockKey});";
            await unlock.ExecuteScalarAsync(CancellationToken.None);
        }
    }

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var runLog = new NotificationRunLog { StartedAt = DateTime.UtcNow };

        _logger.LogInformation("DataCompleteness triggered at {Time}.", runLog.StartedAt);

        Guid? cursor = null;
        string? failure = null;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                List<Guid> organizationIds;
                using (var scope = _scopeFactory.CreateScope())
                {
                    var snapshots = scope.ServiceProvider
                        .GetRequiredService<INotificationSnapshotQueries>();
                    organizationIds = [.. await snapshots
                        .GetActiveOrganizationIdsAsync(cursor, BatchSize, stoppingToken)];
                }

                if (organizationIds.Count == 0)
                    break;

                foreach (var organizationId in organizationIds)
                {
                    if (stoppingToken.IsCancellationRequested)
                        break;

                    try
                    {
                        var result = await ReconcileOrganizationAsync(organizationId, stoppingToken);
                        runLog.Created += result.Created;
                        runLog.Resolved += result.Resolved;
                        runLog.Suppressed += result.Suppressed;
                    }
                    catch (Exception ex)
                    {
                        // One bad organization must not cost the estate its morning run.
                        _logger.LogError(ex,
                            "Failed to reconcile notifications for Organization {OrganizationId}.",
                            organizationId);
                    }

                    runLog.OrganizationsScanned++;
                    cursor = organizationId;
                    runLog.LastOrganizationId = cursor;
                }
            }
        }
        catch (Exception ex)
        {
            failure = ex.Message;
            _logger.LogError(ex, "DataCompleteness run failed after {Scanned} organizations.",
                runLog.OrganizationsScanned);
        }

        stopwatch.Stop();
        runLog.CompletedAt = DateTime.UtcNow;
        runLog.DurationMs = (int)stopwatch.ElapsedMilliseconds;
        runLog.Error = Truncate(failure);

        await PersistRunLogAsync(runLog);

        _logger.LogInformation(
            "DataCompleteness complete. Organizations: {Scanned}, created: {Created}, " +
            "resolved: {Resolved}, suppressed: {Suppressed}, duration: {Duration}ms.",
            runLog.OrganizationsScanned, runLog.Created, runLog.Resolved, runLog.Suppressed,
            runLog.DurationMs);
    }

    /// <summary>
    /// One scope — and so one DbContext — per organization: the snapshot tracks a family's members,
    /// connections and mutes, which would accumulate for the whole estate on a shared context.
    /// </summary>
    private async Task<(int Created, int Resolved, int Suppressed)> ReconcileOrganizationAsync(
        Guid organizationId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var snapshots = scope.ServiceProvider.GetRequiredService<INotificationSnapshotQueries>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var utcNow = DateTime.UtcNow;
        var contexts = await snapshots.BuildContextsForOrganizationAsync(organizationId, utcNow, ct);
        if (contexts.Count == 0)
            return (0, 0, 0);

        var created = 0;
        var resolved = 0;
        var suppressed = 0;

        // Reconciliation is per user: each caregiver's stored set is diffed against their own
        // contexts, so one user's snooze cannot suppress another's notification.
        foreach (var group in contexts.GroupBy(c => c.User.Id))
        {
            var stored = await unitOfWork.Notifications.GetForReconciliationAsync(group.Key, ct);
            var plan = NudgeReconciler.Reconcile([.. group], stored);

            if (plan.ToInsert.Count > 0)
                await unitOfWork.Notifications.AddRangeAsync(plan.ToInsert);

            foreach (var notification in plan.ToUpdate)
                unitOfWork.Notifications.Update(notification);

            created += plan.ToInsert.Count;
            resolved += plan.ResolvedCount;
            suppressed += plan.SuppressedCount;
        }

        if (created > 0 || resolved > 0)
            await unitOfWork.SaveChangesAsync();

        return (created, resolved, suppressed);
    }

    private async Task PersistRunLogAsync(NotificationRunLog runLog)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();
            context.NotificationRunLogs.Add(runLog);
            await context.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            // The run itself succeeded; losing its bookkeeping is not worth surfacing as a failure.
            _logger.LogWarning(ex, "Could not persist the DataCompleteness run log.");
        }
    }

    private static string? Truncate(string? value) =>
        value is null ? null : value.Length <= 2000 ? value : value[..2000];
}
