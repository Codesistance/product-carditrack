using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services;
using CardiTrack.Infrastructure.Settings;
using Microsoft.Extensions.Options;

namespace CardiTrack.Worker.Workers;

/// <summary>
/// Carries out the retention promises nothing else keeps: it erases an account once its thirty-day
/// cancellation window has elapsed, deletes member chat conversations ninety days after their last
/// turn, and clears out wearer device invitations once they have finished mattering.
/// </summary>
/// <remarks>
/// <para>
/// The first two figures are published. The privacy policy and the account-deletion page commit to
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
/// <strong>It erases <em>after</em> the thirty days, never before — and the cadence rounds the
/// same way.</strong> This runs once a day, so an account whose window closes at 05:01 is not
/// picked up by that morning's sweep; it goes the following morning — twenty-four hours later at
/// the outside, <em>provided the day's due set fits inside
/// <see cref="RetentionWorkerOptions.BatchSize"/></em>. It is one batch per sweep, so a backlog
/// larger than that, or an account failing repeatedly, defers by whole days; oldest-request-first
/// ordering is what keeps the wait bounded by the backlog rather than arbitrary. That is a
/// deliberate choice of which promise absorbs the scheduler lag. The alternative
/// — selecting accounts whose window is about to close, or shortening the constant — would erase
/// somebody while the app was still telling them they could cancel, and a caregiver who changes
/// their mind on the last afternoon is a likelier person than an auditor timing the sweep. The
/// published "within 30 days" wording is the half that needs reconciling with this; see
/// <c>docs/technical/manual_erasure_runbook.md</c>.
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
/// <strong>It now revokes the upstream grant too</strong> (issue #148 item 4), through the
/// member cascade, before the <c>DeviceConnections</c> row that holds the token is deleted. A
/// provider that will not answer cannot stop an erasure, so a failure is reported rather than
/// thrown — and reported loudly, because at that point nothing can retry it: the token is gone
/// with the row, and the grant has to be ended from the wearer's own provider account.
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
    private readonly IOptionsMonitor<DeviceInviteOptions> _inviteOptions;
    private readonly ILogger<RetentionWorker> _logger;
    private readonly TimeProvider _timeProvider;

    public RetentionWorker(
        IOptionsMonitor<WorkerOptions> workerOptions,
        IOptionsMonitor<RetentionWorkerOptions> options,
        IOptionsMonitor<DeviceInviteOptions> inviteOptions,
        IServiceScopeFactory scopeFactory,
        ILogger<RetentionWorker> logger,
        TimeProvider? timeProvider = null)
        : base(workerOptions.Get(nameof(RetentionWorker)).CronExpression, logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _inviteOptions = inviteOptions;
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
        await DeleteExpiredInsightsAsync(options, utcNow, stoppingToken);
        await DeleteFinishedDeviceInvitesAsync(options, utcNow, stoppingToken);
    }

    /// <summary>
    /// Deletes stored insights — the alert explanations, baseline readings and trend narratives in
    /// <c>MemberInsights</c> — past <see cref="InsightRetention.MaxAge"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Model-written prose about a named person, so it is persisted AI content in the sense the
    /// DPIA §6.3 means and carries a period of its own. It is swept here rather than dropped with a
    /// partition because this table is ordinary EF-tracked, like <c>MemberChatSessions</c> — the
    /// mechanism the partitioned AI stores use does not reach it.
    /// </para>
    /// <para>
    /// Nothing is lost that cannot be rebuilt: the readings behind an insight are kept far longer
    /// (hourly rollups 13 months, raw daily activity 25), so a member whose row is swept gets a
    /// fresh one on the next pass that finds their data has moved. The period is a constant rather
    /// than a configured dial precisely because it is the policy the DPIA records, not a tuning
    /// knob — see <see cref="InsightRetention"/>.
    /// </para>
    /// </remarks>
    private async Task DeleteExpiredInsightsAsync(
        RetentionWorkerOptions options, DateTime utcNow, CancellationToken ct)
    {
        var cutoff = utcNow - InsightRetention.MaxAge;

        using var scope = _scopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var expired = await unitOfWork.MemberInsights.GetGeneratedBeforeAsync(
            cutoff, InsightRetention.SweepBatchSize);

        if (expired.Count == 0)
        {
            _logger.LogInformation(
                "Retention found no stored insights older than {Days} days.",
                InsightRetention.MaxAge.TotalDays);
            return;
        }

        if (options.DryRun)
        {
            // Ids and scopes only — never the text. A rehearsal an operator reviews must not put
            // the very prose this pass exists to remove into a log that outlives it.
            foreach (var insight in expired)
            {
                _logger.LogInformation(
                    "Retention would delete {Scope} insight {InsightId} for CardiMember " +
                    "{CardiMemberId}, generated {GeneratedAt}, which predates {Cutoff}.",
                    insight.Scope, insight.Id, insight.CardiMemberId, insight.GeneratedAtUtc, cutoff);
            }

            _logger.LogInformation(
                "Retention would delete {Count} stored insight(s) in total.", expired.Count);
            return;
        }

        // Deleted with the cutoff restated, not by key: a baseline or trend pass can rewrite one of
        // these rows in place between the select above and this statement, and a delete by key
        // alone would then throw away an insight generated seconds ago.
        var deleted = await unitOfWork.MemberInsights.DeleteGeneratedBeforeAsync(
            expired.Select(i => i.Id).ToList(), cutoff);

        if (deleted < expired.Count)
        {
            // Worth a line rather than letting the counts silently disagree — the same courtesy
            // the chat pass extends when a conversation gains a turn mid-sweep.
            _logger.LogInformation(
                "Retention left {Count} of {Found} stored insight(s): they were regenerated after "
                + "being selected, so they are no longer expired.",
                expired.Count - deleted, expired.Count);
        }

        _logger.LogInformation(
            "Retention insight pass complete. Insights deleted: {Count}, older than {Days} days.",
            deleted, InsightRetention.MaxAge.TotalDays);
    }

    /// <summary>
    /// Deletes wearer device invitations that finished, or quietly timed out, long enough ago.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The mildest of the three passes, and the only one that deletes nothing anyone would miss: an
    /// invitation holds two ids that already sit together in <c>UserCardiMembers</c>, a brand, the
    /// hash of a token that stopped working weeks ago, and some timestamps. No health data, no
    /// contact detail, nothing about a person that the row's own existence did not already imply.
    /// </para>
    /// <para>
    /// It still belongs here rather than being left to accumulate. The rows are unbounded — one per
    /// invitation ever sent, for the life of the product — and the durable record of the same events
    /// is the audit trail, which keeps them longer and is what anyone investigating would actually
    /// read. A second copy held past the point where it answers a question is retention without a
    /// purpose, which is the failure §7 of the DPIA is about.
    /// </para>
    /// <para>
    /// The figure comes from the <c>DeviceInvites</c> configuration section, the same one the API
    /// builds invitations from, so how long an invitation lives and how long its record is kept
    /// cannot drift into disagreeing about what an invitation is.
    /// </para>
    /// </remarks>
    private async Task DeleteFinishedDeviceInvitesAsync(
        RetentionWorkerOptions options, DateTime utcNow, CancellationToken ct)
    {
        var retentionDays = _inviteOptions.CurrentValue.RetentionDays;
        if (retentionDays <= 0)
        {
            // The same shape of guard as the sweep's own: a negative figure puts the cutoff in the
            // future, where every invitation ever created is due for deletion.
            _logger.LogError(
                "Retention skipped device invites: DeviceInvites:RetentionDays={Days} must be positive.",
                retentionDays);
            return;
        }

        var cutoff = utcNow - TimeSpan.FromDays(retentionDays);

        using var scope = _scopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var sweepable = await unitOfWork.DeviceConnectionInvites.GetSweepableAsync(
            cutoff, options.BatchSize, ct);

        if (sweepable.Count == 0)
        {
            _logger.LogInformation(
                "Retention found no device invites finished or expired before {Cutoff}.", cutoff);
            return;
        }

        if (options.DryRun)
        {
            // Ids and statuses, as the other two passes report theirs. A rehearsal that gives only
            // a count cannot be reviewed.
            foreach (var invite in sweepable)
            {
                _logger.LogInformation(
                    "Retention would delete device invite {InviteId} ({Status}), last relevant at " +
                    "{When}.", invite.Id, invite.Status, invite.ResolvedAt ?? invite.ExpiresAt);
            }

            _logger.LogInformation(
                "Retention would delete {Count} device invite(s) in total.", sweepable.Count);
            return;
        }

        unitOfWork.DeviceConnectionInvites.RemoveRange(sweepable);
        await unitOfWork.SaveChangesAsync();

        _logger.LogInformation(
            "Retention deleted {Count} device invite(s) finished or expired before {Cutoff}.",
            sweepable.Count, cutoff);
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

                // The other half of an incomplete erasure, and the worse one to lose: a grant
                // nothing can end any more, because the token that could have ended it went with
                // the row. Warning, for the same reason an orphaned object is.
                if (report.UnrevokedGrants.Count > 0)
                {
                    _logger.LogWarning(
                        "Retention erased account {UserId} but could not confirm revocation of " +
                        "{Count} device grant(s): {Connections}. They are still live at the " +
                        "provider and must be revoked from the wearer's own account.",
                        userId, report.UnrevokedGrants.Count,
                        string.Join(", ", report.UnrevokedGrants));
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
                // "last activity", not "last turn": a session that never got a turn is dated
                // from when it was started, and a rehearsal an operator is meant to review must
                // not explain an empty shell as though it had a conversation in it.
                _logger.LogInformation(
                    "Retention would delete chat conversation {SessionId}, whose last activity " +
                    "— its newest turn, or its start time if it has none — predates {Cutoff}.",
                    sessionId, cutoff);
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
