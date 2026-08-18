using CardiTrack.Application.Interfaces.Clients;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CardiTrack.Worker.Workers;

/// <summary>
/// Enforcement backstop behind the best-effort photo-blob deletes in <c>CardiMemberService</c>
/// (photo replace, photo removal, member soft delete — all of which delete the old blob only
/// after the save lands, and swallow a delete that fails). A full-face photo is Tier 1 data and
/// "the blob must not outlive the membership" (docs/technical/data_protection_architecture.md §5)
/// is only a guarantee if something checks — this is the something: it deletes every bucket
/// object no active member's <c>PhotoObjectName</c> references once the object is older than
/// 24 hours, and clears the photo of any soft-deleted member a crashed removal left one on.
/// </summary>
/// <remarks>
/// <para>
/// The 24-hour grace covers the crash window in the other direction: an object whose upload
/// succeeded but whose DB save has not happened yet is unreferenced without being orphaned.
/// Every upload gets a fresh GUID name, so an object past the grace window that no active row
/// names can never become referenced again — the reference-set diff is race-free by naming.
/// </para>
/// <para>
/// Destructive, so it carries the §5.2 constraints: a non-blocking Postgres advisory lock (up to
/// 3 Cloud Run instances; one sweep is the only useful result), a per-object error boundary, a
/// <see cref="OrphanedPhotoCleanupOptions.DryRun"/> rehearsal mode, and a
/// scanned/orphaned/deleted/failed summary. Log lines carry object names only — never a signed
/// URL, which is a bearer capability to the photo itself.
/// </para>
/// </remarks>
public class OrphanedPhotoCleanupWorker : CronBackgroundService
{
    /// <summary>
    /// Arbitrary but fixed — next in sequence after <see cref="DataCompletenessWorker"/>'s key.
    /// Postgres advisory locks are namespaced only by the number, so it must not collide with
    /// another job's.
    /// </summary>
    private const long AdvisoryLockKey = 8_472_100_002;

    /// <summary>
    /// Far longer than any upload-to-save gap, so an in-flight photo save is never reaped —
    /// the same stance as <see cref="OrphanedOrganizationCleanupWorker"/>'s MinAge.
    /// </summary>
    private static readonly TimeSpan MinAge = TimeSpan.FromHours(24);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IProfilePhotoStorage _photoStorage;
    private readonly IOptionsMonitor<OrphanedPhotoCleanupOptions> _options;
    private readonly ILogger<OrphanedPhotoCleanupWorker> _logger;
    private readonly TimeProvider _timeProvider;

    public OrphanedPhotoCleanupWorker(
        IOptionsMonitor<WorkerOptions> workerOptions,
        IOptionsMonitor<OrphanedPhotoCleanupOptions> options,
        IServiceScopeFactory scopeFactory,
        IProfilePhotoStorage photoStorage,
        ILogger<OrphanedPhotoCleanupWorker> logger,
        TimeProvider? timeProvider = null)
        : base(workerOptions.Get(nameof(OrphanedPhotoCleanupWorker)).CronExpression, logger)
    {
        _scopeFactory = scopeFactory;
        _photoStorage = photoStorage;
        _options = options;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    protected override async Task ExecuteJobAsync(CancellationToken stoppingToken)
    {
        using var lockScope = _scopeFactory.CreateScope();
        var lockContext = lockScope.ServiceProvider.GetRequiredService<CardiTrackDbContext>();

        // Session-scoped and non-blocking, exactly as DataCompletenessWorker takes it: an
        // instance that cannot get the lock skips the run rather than queueing behind it and
        // sweeping the same bucket again afterwards.
        var connection = lockContext.Database.GetDbConnection();
        await connection.OpenAsync(stoppingToken);

        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT pg_try_advisory_lock({AdvisoryLockKey});";
        var acquired = (bool?)await command.ExecuteScalarAsync(stoppingToken) ?? false;

        if (!acquired)
        {
            _logger.LogInformation(
                "OrphanedPhotoCleanup skipped — another instance holds the advisory lock.");
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
        var dryRun = _options.CurrentValue.DryRun;
        var utcNow = _timeProvider.GetUtcNow();

        _logger.LogInformation(
            "OrphanedPhotoCleanup triggered at {Time} (dry run: {DryRun}).", utcNow, dryRun);

        using var scope = _scopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var scanned = 0;
        var orphaned = 0;
        var deleted = 0;
        var failed = 0;

        // Soft-deleted leftovers first: these are named by a DB row, so they are decided by the
        // row, not by the listing diff — and clearing them before the reference set is loaded
        // costs nothing because an inactive member's photo is never in that set anyway.
        var handled = await ClearSoftDeletedLeftoversAsync(
            unitOfWork, dryRun, onOrphaned: () => orphaned++, onDeleted: () => deleted++,
            onFailed: () => failed++, stoppingToken);

        var live = (await unitOfWork.CardiMembers.GetActivePhotoObjectNamesAsync())
            .ToHashSet(StringComparer.Ordinal);

        await foreach (var (objectName, createdAt) in _photoStorage.ListAsync(stoppingToken))
        {
            scanned++;

            if (live.Contains(objectName) || handled.Contains(objectName))
                continue;

            // Younger than the grace window means possibly an upload whose save is still in
            // flight — unreferenced today, referenced by tonight. Left for the next run.
            if (utcNow - createdAt < MinAge)
                continue;

            orphaned++;

            if (dryRun)
            {
                _logger.LogInformation(
                    "OrphanedPhotoCleanup would delete unreferenced object {ObjectName} " +
                    "(created {CreatedAt}).", objectName, createdAt);
                continue;
            }

            try
            {
                await _photoStorage.DeleteAsync(objectName, stoppingToken);
                deleted++;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One undeletable blob must not cost the bucket its sweep; it is still
                // unreferenced next run. Object name only — never a URL.
                failed++;
                _logger.LogWarning(ex,
                    "OrphanedPhotoCleanup failed to delete object {ObjectName}; the next run retries it.",
                    objectName);
            }
        }

        _logger.LogInformation(
            "OrphanedPhotoCleanup complete. Scanned: {Scanned}, orphaned: {Orphaned}, " +
            "deleted: {Deleted}, failed: {Failed} (dry run: {DryRun}).",
            scanned, orphaned, deleted, failed, dryRun);
    }

    /// <summary>
    /// Deletes the blob and nulls the column for soft-deleted members still carrying a photo.
    /// Normally finds nothing — the removal path clears the column before it saves — so every hit
    /// logs at Warning: it means a removal crashed between the save and the blob delete, which is
    /// worth investigating, not just cleaning. Blob first, column second; either half failing
    /// leaves a state the next run (or the listing diff, once the blob is 24h old) repairs.
    /// </summary>
    /// <returns>Object names dealt with here, so the listing pass does not double-count them.</returns>
    private async Task<HashSet<string>> ClearSoftDeletedLeftoversAsync(
        IUnitOfWork unitOfWork, bool dryRun, Action onOrphaned, Action onDeleted, Action onFailed,
        CancellationToken ct)
    {
        var handled = new HashSet<string>(StringComparer.Ordinal);

        foreach (var member in await unitOfWork.CardiMembers.GetInactiveWithPhotoAsync())
        {
            ct.ThrowIfCancellationRequested();

            var objectName = member.PhotoObjectName!;
            onOrphaned();

            if (dryRun)
            {
                _logger.LogInformation(
                    "OrphanedPhotoCleanup would delete object {ObjectName} and clear the photo " +
                    "of soft-deleted member {CardiMemberId}.", objectName, member.Id);
                handled.Add(objectName);
                continue;
            }

            try
            {
                await _photoStorage.DeleteAsync(objectName, ct);

                member.PhotoObjectName = null;
                member.UpdatedDate = DateTime.UtcNow;
                unitOfWork.CardiMembers.Update(member);
                await unitOfWork.SaveChangesAsync();

                onDeleted();
                handled.Add(objectName);
                _logger.LogWarning(
                    "OrphanedPhotoCleanup deleted the photo a crashed removal left on " +
                    "soft-deleted member {CardiMemberId} (object {ObjectName}).",
                    member.Id, objectName);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                onFailed();
                _logger.LogWarning(ex,
                    "OrphanedPhotoCleanup failed to clear the photo of soft-deleted member " +
                    "{CardiMemberId} (object {ObjectName}); the next run retries it.",
                    member.Id, objectName);
            }
        }

        return handled;
    }
}
