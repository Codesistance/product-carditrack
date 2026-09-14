using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services;
using Microsoft.Extensions.Options;

namespace CardiTrack.Worker.Workers;

/// <summary>
/// Carries out the two retention promises nothing else keeps: it erases an account once its
/// thirty-day cancellation window has elapsed, and it deletes member chat conversations ninety
/// days after their last turn.
/// </summary>
/// <remarks>
/// <para>
/// Both figures are published. The privacy policy and the account-deletion page commit to
/// completing a deletion <em>within</em> thirty days of a verified request, and the chat retention
/// period is stated at §6.3 of the DPIA and in the policy. Until this worker, both were kept by
/// hand — <c>docs/technical/manual_erasure_runbook.md</c> is that procedure, and an operator
/// remembering to run it was the entire control. An unwritten obligation gets forgotten, and an
/// unmet published deletion promise is the failure mode the FTC actions in
/// <c>docs/solution_manifest.md</c> Risk 3 turned on.
/// </para>
/// <para>
/// <strong>The grace period is the caregiver's, not this worker's.</strong> The threshold is
/// <see cref="UserService.DeletionGracePeriod"/> — the same constant the API uses to tell a
/// caregiver the date they can still cancel until. Reading it from configuration would let the
/// two disagree, and the direction that disagreement fails in is erasing an account somebody
/// still had the right to keep.
/// </para>
/// <para>
/// The worker itself only schedules, bounds and reports; both cascades live behind Application
/// ports (<see cref="IAccountErasureService"/>, <see cref="IChatRetentionService"/>) so what they
/// delete can be tested against a real database without a host.
/// </para>
/// <para>
/// Destructive and irreversible, so it carries the §5.2 constraints in full: a non-blocking
/// Postgres advisory lock (up to 3 Cloud Run instances; one sweep is the only useful result), a
/// per-account error boundary so one bad account does not cost the rest their sweep, a
/// <see cref="RetentionWorkerOptions.DryRun"/> rehearsal mode, a bounded batch, and a
/// due/erased/failed summary. Log lines carry ids and counts only — never a member's name, a
/// reading, or a line of a conversation.
/// </para>
/// <para>
/// <strong>What it still does not do:</strong> revoke the upstream OAuth grant. Erasing an
/// account deletes its <c>DeviceConnections</c> rows, which stops collection, but the token stays
/// live at Google until it expires. That is issue #148 item 4 and remains a manual step in the
/// runbook.
/// </para>
/// </remarks>
public class RetentionWorker : CronBackgroundService
{
    /// <summary>
    /// Arbitrary but fixed — next free in the sequence after
    /// <see cref="HistoryRepullWorker"/>'s key. Postgres advisory locks are namespaced only by
    /// the number, so it must not collide with another job's.
    /// </summary>
    private const long AdvisoryLockKey = 8_472_100_005;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<RetentionWorkerOptions> _options;
    private readonly ILogger<RetentionWorker> _logger;
    private readonly TimeProvider _timeProvider;

    public RetentionWorker(
        IOptionsMonitor<WorkerOptions> workerOptions,
        IOptionsMonitor<RetentionWorkerOptions> options,
        IServiceScopeFactory scopeFactory,
        ILogger<RetentionWorker> logger,
        TimeProvider? timeProvider = null)
        : base(workerOptions.Get(nameof(RetentionWorker)).CronExpression, logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    protected override async Task ExecuteJobAsync(CancellationToken stoppingToken)
    {
        var ran = await AdvisoryLock.TryRunAsync(
            _scopeFactory, AdvisoryLockKey, () => SweepAsync(stoppingToken), stoppingToken);

        if (!ran)
            _logger.LogInformation("Retention skipped — another instance holds the advisory lock.");
    }

    /// <summary>The sweep behind the advisory lock. Protected so tests can drive it directly.</summary>
    protected async Task SweepAsync(CancellationToken stoppingToken)
    {
        var options = _options.CurrentValue;
        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;

        // Checked before anything is read, let alone deleted, the same way
        // PartitionMaintenanceWorker gates its own retention values. The failure this prevents is
        // not a crash: a negative ChatRetentionDays puts the cutoff in the *future*, at which
        // point every conversation on the platform is expired and a job whose whole purpose is to
        // delete them would do exactly that, quietly and correctly by its own arithmetic. A zero
        // or negative BatchSize is the harmless twin — it silently retains everything instead —
        // but a retention job doing nothing is also worth a loud line rather than a clean one.
        if (options.ChatRetentionDays <= 0 || options.BatchSize <= 0)
        {
            _logger.LogError(
                "Retention skipped: invalid configuration (ChatRetentionDays={ChatDays}, " +
                "BatchSize={BatchSize}). Both must be positive.",
                options.ChatRetentionDays, options.BatchSize);
            return;
        }

        _logger.LogInformation(
            "Retention triggered at {Time} (dry run: {DryRun}).", utcNow, options.DryRun);

        await EraseDueAccountsAsync(options, utcNow, stoppingToken);
        await DeleteExpiredChatSessionsAsync(options, utcNow, stoppingToken);
    }

    /// <summary>
    /// Erases every account whose thirty-day cancellation window has closed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each account gets its own scope and its own error boundary. The cascade is long and
    /// re-entrant — the user row and its <c>DeletionRequestedAtUtc</c> survive until the final
    /// step — so an account that throws halfway is simply due again next run, with most of its
    /// rows already gone. A failure that recurs run after run is the signal to look at, which is
    /// why the log line is at Error.
    /// </para>
    /// <para>
    /// <strong>A cancellation cannot be lost in the gap between selecting an account and erasing
    /// it</strong>, because cancelling and erasing are decided by the same boundary:
    /// <c>UserService.CancelDeletionAsync</c> refuses once <c>UtcNow</c> has reached
    /// <c>requestedAt + DeletionGracePeriod</c>, and this pass selects exactly the accounts that
    /// have passed it. An account the worker can see is one the API will no longer un-delete. The
    /// re-read below is belt to that braces — it costs one query and it is the assertion that
    /// keeps the two halves honest if either boundary is ever changed alone.
    /// </para>
    /// </remarks>
    private async Task EraseDueAccountsAsync(
        RetentionWorkerOptions options, DateTime utcNow, CancellationToken ct)
    {
        var cutoff = utcNow - UserService.DeletionGracePeriod;

        List<Guid> due;
        using (var scope = _scopeFactory.CreateScope())
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            due = (await unitOfWork.Users.GetAccountsDueForErasureAsync(cutoff, options.BatchSize))
                .ToList();
        }

        if (due.Count == 0)
        {
            _logger.LogInformation("Retention found no accounts past their cancellation window.");
            return;
        }

        var erased = 0;
        var failed = 0;

        foreach (var userId in due)
        {
            ct.ThrowIfCancellationRequested();

            if (options.DryRun)
            {
                _logger.LogInformation(
                    "Retention would erase account {UserId}, whose cancellation window closed " +
                    "before {Cutoff}.", userId, cutoff);
                continue;
            }

            try
            {
                using var accountScope = _scopeFactory.CreateScope();
                var unitOfWork = accountScope.ServiceProvider.GetRequiredService<IUnitOfWork>();

                // See the remarks above: this should never be empty for an account this pass
                // selected. If it is, the two boundaries have drifted and the safe reading is
                // the caregiver's.
                var account = await unitOfWork.Users.GetByIdAsync(userId);
                if (account?.DeletionRequestedAtUtc is not { } requestedAt || requestedAt > cutoff)
                {
                    _logger.LogWarning(
                        "Retention skipped account {UserId}: its deletion request is no longer " +
                        "outstanding. Cancelling after the window closes should be impossible — " +
                        "check UserService.CancelDeletionAsync against this worker's cutoff.",
                        userId);
                    continue;
                }

                var erasure = accountScope.ServiceProvider.GetRequiredService<IAccountErasureService>();
                var report = await erasure.EraseAsync(userId, ct);
                erased++;

                // Warning, not Information: an orphaned object means the erasure is incomplete and
                // somebody has to finish it by hand. It must not read as a clean run.
                if (report.OrphanedObjects.Count > 0)
                {
                    _logger.LogWarning(
                        "Retention erased account {UserId} but left {Count} storage object(s) " +
                        "behind; they need deleting by hand: {Objects}.",
                        userId, report.OrphanedObjects.Count,
                        string.Join(", ", report.OrphanedObjects));
                }

                _logger.LogInformation(
                    "Retention erased account {UserId}. Members erased: {Erased}, members left " +
                    "with their other caregivers: {Released}.",
                    userId, report.MembersErased.Count, report.MembersReleased.Count);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogError(ex,
                    "Retention failed to erase account {UserId}; it stays due and the next run " +
                    "retries it. A repeat means the published thirty-day promise is being missed.",
                    userId);
            }
        }

        _logger.LogInformation(
            "Retention account pass complete. Due: {Due}, erased: {Erased}, failed: {Failed} " +
            "(dry run: {DryRun}).", due.Count, erased, failed, options.DryRun);
    }

    /// <summary>
    /// Deletes whole chat conversations whose newest turn is older than the retention period.
    /// </summary>
    private async Task DeleteExpiredChatSessionsAsync(
        RetentionWorkerOptions options, DateTime utcNow, CancellationToken ct)
    {
        var cutoff = utcNow - TimeSpan.FromDays(options.ChatRetentionDays);

        using var scope = _scopeFactory.CreateScope();
        var retention = scope.ServiceProvider.GetRequiredService<IChatRetentionService>();

        var expired = await retention.FindExpiredSessionsAsync(cutoff, options.BatchSize, ct);

        if (expired.Count == 0)
        {
            _logger.LogInformation(
                "Retention found no chat conversations older than {Days} days.",
                options.ChatRetentionDays);
            return;
        }

        if (options.DryRun)
        {
            // Every id, as the account pass does. A rehearsal that reports only a count cannot be
            // reviewed — an operator has no way to tell which conversations the real run will
            // take. Ids only; nothing of what was said.
            foreach (var sessionId in expired)
            {
                _logger.LogInformation(
                    "Retention would delete chat conversation {SessionId}, whose last turn " +
                    "predates {Cutoff}.", sessionId, cutoff);
            }

            _logger.LogInformation(
                "Retention would delete {Count} chat conversation(s) in total.", expired.Count);
            return;
        }

        var report = await retention.DeleteSessionsAsync(expired, cutoff, ct);

        // Fewer than were found means somebody replied between the two calls and the service
        // rightly declined to take their thread — worth a line, because otherwise the counts
        // simply disagree with no explanation.
        if (report.Sessions < expired.Count)
        {
            _logger.LogInformation(
                "Retention left {Count} of {Found} chat conversation(s): a turn was written " +
                "after they were selected, so they are no longer expired.",
                expired.Count - report.Sessions, expired.Count);
        }

        _logger.LogInformation(
            "Retention chat pass complete. Conversations deleted: {Sessions}, turns: {Turns}, " +
            "usage rows: {Usages}, older than {Days} days.",
            report.Sessions, report.Turns, report.Usages, options.ChatRetentionDays);
    }
}
