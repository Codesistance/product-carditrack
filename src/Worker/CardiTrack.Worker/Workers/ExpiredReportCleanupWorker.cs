using CardiTrack.Application.Interfaces.Clients;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Persistence;
using CardiTrack.Infrastructure.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CardiTrack.Worker.Workers;

/// <summary>
/// Retention and repair for health-data exports: deletes exports past their download window, and
/// fails out generations that never finished.
/// </summary>
/// <remarks>
/// <para>
/// The cache-backed design got both of these for free from a TTL — and paid for them by losing
/// finished reports too. Durable storage keeps the report, which makes the expiry someone's job:
/// this worker's. An export is a full identified health record sitting in a bucket, so a retention
/// window nothing enforces is not a retention window.
/// </para>
/// <para>
/// It lives here and not in the API because <c>CLAUDE.md</c> puts non-AI background jobs and DB
/// polling in <c>CardiTrack.Worker</c> exclusively — retention and cleanup are named in that rule.
/// </para>
/// <para>
/// Destructive, so it carries the §5.2 constraints of its sibling <see cref="OrphanedPhotoCleanupWorker"/>:
/// a non-blocking Postgres advisory lock (one sweep is the only useful result across instances), a
/// per-report error boundary, a <see cref="ExpiredReportCleanupOptions.DryRun"/> rehearsal mode,
/// and a scanned/deleted/failed summary. Log lines carry report ids and object names only — never
/// a member name, a filename built from one, or any of the content.
/// </para>
/// </remarks>
public class ExpiredReportCleanupWorker : CronBackgroundService
{
    /// <summary>
    /// Arbitrary but fixed — next in sequence after <see cref="OrphanedPhotoCleanupWorker"/>'s key.
    /// Postgres advisory locks are namespaced only by the number, so it must not collide with
    /// another job's.
    /// </summary>
    private const long AdvisoryLockKey = 8_472_100_003;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IReportStorage _reportStorage;
    private readonly ReportStorageOptions _storageOptions;
    private readonly IOptionsMonitor<ExpiredReportCleanupOptions> _options;
    private readonly ILogger<ExpiredReportCleanupWorker> _logger;
    private readonly TimeProvider _timeProvider;

    public ExpiredReportCleanupWorker(
        IOptionsMonitor<WorkerOptions> workerOptions,
        IOptionsMonitor<ExpiredReportCleanupOptions> options,
        IServiceScopeFactory scopeFactory,
        IReportStorage reportStorage,
        ReportStorageOptions storageOptions,
        ILogger<ExpiredReportCleanupWorker> logger,
        TimeProvider? timeProvider = null)
        : base(workerOptions.Get(nameof(ExpiredReportCleanupWorker)).CronExpression, logger)
    {
        _scopeFactory = scopeFactory;
        _reportStorage = reportStorage;
        _storageOptions = storageOptions;
        _options = options;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    protected override async Task ExecuteJobAsync(CancellationToken stoppingToken)
    {
        using var lockScope = _scopeFactory.CreateScope();
        var lockContext = lockScope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();

        // Session-scoped and non-blocking, as its siblings take it: an instance that cannot get
        // the lock skips the run rather than queueing behind it and sweeping the same rows again.
        var connection = lockContext.Database.GetDbConnection();
        await connection.OpenAsync(stoppingToken);

        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT pg_try_advisory_lock({AdvisoryLockKey});";
        var acquired = (bool?)await command.ExecuteScalarAsync(stoppingToken) ?? false;

        if (!acquired)
        {
            _logger.LogInformation(
                "ExpiredReportCleanup skipped — another instance holds the advisory lock.");
            return;
        }

        try
        {
            await SweepAsync(stoppingToken);
        }
        finally
        {
            await using var unlock = connection.CreateCommand();
            unlock.CommandText = $"SELECT pg_advisory_unlock({AdvisoryLockKey});";
            await unlock.ExecuteScalarAsync(CancellationToken.None);
        }
    }

    /// <summary>The sweep behind the advisory lock. Protected so tests can drive it directly.</summary>
    protected async Task SweepAsync(CancellationToken stoppingToken)
    {
        var options = _options.CurrentValue;
        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;

        _logger.LogInformation(
            "ExpiredReportCleanup triggered at {Time} (dry run: {DryRun}).", utcNow, options.DryRun);

        using var scope = _scopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var abandoned = await FailAbandonedAsync(unitOfWork, utcNow, options, stoppingToken);
        var (scanned, deleted, failed) = await DeleteExpiredAsync(
            unitOfWork, utcNow, options, stoppingToken);

        _logger.LogInformation(
            "ExpiredReportCleanup complete. Expired scanned: {Scanned}, deleted: {Deleted}, " +
            "failed: {Failed}, abandoned generations failed out: {Abandoned} (dry run: {DryRun}).",
            scanned, deleted, failed, abandoned, options.DryRun);
    }

    /// <summary>
    /// Marks generations that never finished as failed. Without this a caregiver polls a report
    /// forever: the row says Pending and the task that would have completed it died with its host.
    /// Not destructive of anything but a status, so it runs before the deletes and does not share
    /// their per-object error boundary.
    /// </summary>
    private async Task<int> FailAbandonedAsync(
        IUnitOfWork unitOfWork, DateTime utcNow, ExpiredReportCleanupOptions options,
        CancellationToken ct)
    {
        var cutoff = utcNow - _storageOptions.GenerationTimeout;
        var stale = await unitOfWork.Reports.GetStalePendingAsync(cutoff, options.BatchSize, ct);

        if (stale.Count == 0)
            return 0;

        // Warning, not Information: a generation dying mid-flight is a host that went away or a
        // provider call that hung, and it is worth knowing about rather than just tidying.
        _logger.LogWarning(
            "ExpiredReportCleanup found {Count} report(s) still pending since before {Cutoff}.",
            stale.Count, cutoff);

        if (options.DryRun)
            return stale.Count;

        foreach (var report in stale)
        {
            report.Status = ReportStatus.Failed;
            report.CompletedAt = utcNow;
            report.Error = "Report generation failed. Please try again.";
            unitOfWork.Reports.Update(report);
        }

        await unitOfWork.SaveChangesAsync();
        return stale.Count;
    }

    private async Task<(int Scanned, int Deleted, int Failed)> DeleteExpiredAsync(
        IUnitOfWork unitOfWork, DateTime utcNow, ExpiredReportCleanupOptions options,
        CancellationToken ct)
    {
        var expired = await unitOfWork.Reports.GetExpiredAsync(utcNow, options.BatchSize, ct);

        var deleted = 0;
        var failed = 0;
        var removable = new List<Report>(expired.Count);

        foreach (var report in expired)
        {
            ct.ThrowIfCancellationRequested();

            if (options.DryRun)
            {
                _logger.LogInformation(
                    "ExpiredReportCleanup would delete report {ReportId} (expired {ExpiresAt}, " +
                    "object {ObjectName}).", report.Id, report.ExpiresAt, report.ObjectName ?? "none");
                continue;
            }

            // Blob first, row second. If the row went first, a failed blob delete would leave an
            // object nothing names — a health record in a bucket with no record that it exists.
            // This order can only ever leave a row whose object is already gone, which the next
            // run clears and which reads as expired in the meantime either way.
            try
            {
                if (report.ObjectName is not null)
                    await _reportStorage.DeleteAsync(report.ObjectName, ct);

                removable.Add(report);
                deleted++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One undeletable object must not cost the sweep; it is still expired next run.
                failed++;
                _logger.LogWarning(ex,
                    "ExpiredReportCleanup failed to delete object {ObjectName} for report " +
                    "{ReportId}; the next run retries it.", report.ObjectName, report.Id);
            }
        }

        if (removable.Count > 0)
        {
            unitOfWork.Reports.RemoveRange(removable);
            await unitOfWork.SaveChangesAsync();
        }

        return (expired.Count, deleted, failed);
    }
}
