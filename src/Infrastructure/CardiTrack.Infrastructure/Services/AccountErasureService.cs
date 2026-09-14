using CardiTrack.Application.Interfaces.Clients;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CardiTrack.Infrastructure.Services;

/// <summary>
/// The account-scoped erasure cascade, automated from the account-scoped and caregiver-only
/// tables in <c>docs/technical/manual_erasure_runbook.md</c> (rows 27-42).
/// </summary>
/// <remarks>
/// <para>
/// Written against the <see cref="DbContext"/> for the same reason
/// <see cref="MemberErasureService"/> is, and it delegates every member-scoped row to that
/// service rather than repeating the twenty-nine steps: one cascade, one order, one place that
/// has to stay in step with the runbook.
/// </para>
/// <para>
/// <strong>Not one transaction, deliberately.</strong> Each member is erased in its own
/// transaction and the account's own rows in a last one. A single transaction spanning a
/// household of members would hold every partition of <c>GranularMetricHours</c> open for the
/// length of the sweep, and it would buy nothing: nothing here is reversible in any sense a data
/// subject would recognise, so an all-or-nothing boundary protects no invariant. What matters
/// instead is that a half-finished erasure is <em>resumable</em>, and it is — the user row and
/// its <c>DeletionRequestedAtUtc</c> survive until the final step, so the next run picks the
/// account up again and re-runs the cascade over rows that are mostly already gone.
/// </para>
/// <para>
/// <strong>Revoking upstream OAuth is not done here</strong> (issue #148 item 4). Member erasure
/// deletes the <c>DeviceConnections</c> row, which stops collection, but the token stays live at
/// Google until it expires. The runbook's "revoke before deleting" step is still a manual duty.
/// </para>
/// <para>
/// <strong>Ingestion cannot write rows behind the cascade</strong>, though nothing here enforces
/// that directly. A member left without a caregiver by a pending deletion is excluded from sync
/// scheduling at the four selection sites in <c>DeviceConnectionRepository</c> <em>and</em> from
/// <c>InactivityDetectionService</c>, which is the one pass that reaches the sync service without
/// going through that scheduler — it probes a silent device before alerting, and that probe
/// writes rows. Both have applied since the request was made, thirty days before this runs, so a
/// sync would have to have been in flight for the whole window to insert an <c>ActivityLogs</c>
/// row after the delete.
/// </para>
/// <para>
/// Those gates, not this service, are what make the ordering safe, and they are a list rather
/// than an invariant: <strong>any new path to <c>IDeviceSyncService</c> has to apply the same
/// rule</strong> — <c>IUserCardiMemberRepository.HasWatcherNotAwaitingDeletionAsync</c> is the
/// reusable form — or this becomes a real race with no foreign key to stop it. The first version
/// of this paragraph claimed the four scheduler sites were the whole story; they were not.
/// </para>
/// <para>
/// <strong>The races this does not close.</strong> Two reads decide destructive work: whether
/// anyone else still watches a member, and whether anyone else is still in the organisation.
/// Both are re-asked as late as they can be — the member check immediately before each erasure,
/// the organisation check inside the final transaction — but neither takes a lock, so under
/// <c>READ COMMITTED</c> a link or a user created in the remaining gap is not seen. That gap is
/// currently unreachable: <c>CardiMemberService</c> is the only code that creates a
/// <c>UserCardiMember</c>, and it creates exactly one, for the caregiver adding the member, so no
/// member has a second watcher and no user joins an existing organisation. **When family sharing
/// ships (R2), this needs a real claim** — a row lock on the user, or a status column the erasure
/// transitions through — not a re-read.
/// </para>
/// </remarks>
public class AccountErasureService : IAccountErasureService
{
    private readonly CardiTrackDbContext _db;
    private readonly IMemberErasureService _members;
    private readonly IReportStorage _reports;
    private readonly ILogger<AccountErasureService> _logger;

    public AccountErasureService(
        CardiTrackDbContext db,
        IMemberErasureService members,
        IReportStorage reports,
        ILogger<AccountErasureService> logger)
    {
        _db = db;
        _members = members;
        _reports = reports;
        _logger = logger;
    }

    public async Task<AccountErasureReport> EraseAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);

        if (user is null)
        {
            // Already gone — a retry after a run that got as far as the final step. An empty
            // report is the truth rather than an error: there is nothing left to erase.
            _logger.LogInformation(
                "Account erasure for {UserId} found no user row; treating as already complete.",
                userId);
            return new AccountErasureReport(userId, [], [], [], []);
        }

        var (toErase, toRelease) = await PartitionMembersAsync(userId, ct);

        var rows = new List<(string Table, int Rows)>();
        var orphaned = new List<string>();
        var erased = new List<Guid>();
        var released = toRelease.ToList();

        // The members about to go. Named as a set because the organisation check below has to
        // ignore them: they still have rows at that point in the loop's own transaction history,
        // but they are not what keeps an organisation alive.
        var toEraseSet = toErase.ToHashSet();

        foreach (var memberId in toErase)
        {
            // The same rule as below, applied from the second member on: once one cascade has
            // committed, its orphan names live only in the list above, and a cancellation while
            // erasing the next member would discard them. Before the first commit there is
            // nothing to lose, so a shutdown can still stop the run cleanly there.
            var step = erased.Count > 0 ? CancellationToken.None : ct;
            step.ThrowIfCancellationRequested();

            // Asked again, immediately before the irreversible part. The caller decided this
            // member was unwatched some time ago — a whole cascade ago, if this is the second
            // member in the loop — and erasing somebody a relative started watching in the
            // meantime is the one mistake here that cannot be explained to them afterwards.
            if (await IsStillWatchedByAnotherAsync(memberId, userId, step))
            {
                _logger.LogWarning(
                    "Account erasure for {UserId} skipped CardiMember {CardiMemberId}: another "
                    + "caregiver started watching them after the cascade began. Released instead.",
                    userId, memberId);
                released.Add(memberId);
                continue;
            }

            var report = await _members.EraseAsync(memberId, step);
            erased.Add(memberId);
            rows.AddRange(report.RowsByTable.Select(r => ($"{r.Table} ({memberId})", r.Rows)));
            orphaned.AddRange(report.OrphanedObjects);
        }

        // From here the run is uninterruptible if any member has already been erased. Their
        // cascades are committed, and the only record of what they left behind is the in-memory
        // `orphaned` list — a throw anywhere below discards it, and on the retry the member and
        // its Reports rows are gone, so a bucket object nobody can name survives instead of one
        // the report names. Nothing is lost by finishing: what remains is a short query, one
        // transaction and a handful of storage deletes. With nothing erased yet there is nothing
        // to protect, so the caller's token still applies.
        var rest = erased.Count > 0 ? CancellationToken.None : ct;

        // Read before deleting, as the member cascade does: once the rows are gone nothing
        // remembers which objects they named.
        var reportObjects = await _db.Reports
            .Where(r => r.OwnerUserId == userId && r.ObjectName != null)
            .Select(r => r.ObjectName!)
            .ToListAsync(rest);

        await using var transaction = await _db.Database.BeginTransactionAsync(rest);
        try
        {
            // The same definition OrphanedOrganizationCleanupWorker uses: an organisation is
            // spent when it has no users **and no CardiMembers**. The members half is not
            // theoretical — a released member keeps an OrganizationId pointing here, and with no
            // foreign key behind it, deleting the organisation would leave them unreachable by
            // every organisation-scoped read rather than merely unwatched by this caregiver.
            // Asked inside the transaction, and as late as possible, because afterwards the
            // question cannot be asked at all.
            var organizationSpent =
                !await _db.Users.AnyAsync(
                    u => u.OrganizationId == user.OrganizationId && u.Id != userId, rest)
                && !await _db.CardiMembers.AnyAsync(
                    m => m.OrganizationId == user.OrganizationId && !toEraseSet.Contains(m.Id), rest);

            async Task Step<T>(string table, IQueryable<T> query) where T : class =>
                rows.Add((table, await query.ExecuteDeleteAsync(rest)));

            // Null, do not delete (runbook row 40). The alert and the answer belong to the
            // member, who may still be being watched by somebody else; only the name of the
            // caregiver who touched them goes.
            rows.Add(("Alerts.AcknowledgedByUserId (nulled)", await _db.Alerts
                .Where(a => a.AcknowledgedByUserId == userId)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.AcknowledgedByUserId, (Guid?)null), rest)));
            rows.Add(("MemberQuestionnaires.AnsweredByUserId (nulled)", await _db.MemberQuestionnaires
                .Where(q => q.AnsweredByUserId == userId)
                .ExecuteUpdateAsync(s => s.SetProperty(q => q.AnsweredByUserId, (Guid?)null), rest)));

            // Their transcripts about members other people still watch. Turns and usages cascade
            // from the session, but go explicitly so the counts are real rather than inferred.
            await Step("MemberChatTurnUsages", _db.MemberChatTurnUsages
                .Where(u => _db.MemberChatSessions
                    .Any(s => s.UserId == userId
                              && _db.MemberChatTurns.Any(t => t.SessionId == s.Id && t.Id == u.TurnId))));
            await Step("MemberChatTurns", _db.MemberChatTurns
                .Where(t => _db.MemberChatSessions.Any(s => s.Id == t.SessionId && s.UserId == userId)));
            await Step("MemberChatSessions", _db.MemberChatSessions.Where(s => s.UserId == userId));

            // RequestedByUserId is required, so these cannot be nulled the way an
            // acknowledgement can. The member's 48-hour re-pull cooldown resets — a smaller
            // cost than keeping a row naming an erased caregiver.
            await Step("DeviceHistoryRepulls", _db.DeviceHistoryRepulls
                .Where(r => r.RequestedByUserId == userId));

            await Step("NotificationDeliveries", _db.NotificationDeliveries.Where(x => x.UserId == userId));
            await Step("NotificationMutes", _db.NotificationMutes.Where(x => x.UserId == userId));
            await Step("Notifications", _db.Notifications.Where(x => x.UserId == userId));
            await Step("PushDeviceTokens", _db.PushDeviceTokens.Where(x => x.UserId == userId));
            await Step("NotificationPreferences", _db.NotificationPreferences.Where(x => x.UserId == userId));
            await Step("ExportConsents", _db.ExportConsents.Where(x => x.OwnerUserId == userId));
            await Step("Reports", _db.Reports.Where(x => x.OwnerUserId == userId));
            await Step("CardiMemberCreationKeys", _db.CardiMemberCreationKeys.Where(x => x.UserId == userId));
            await Step("UserCardiMembers", _db.UserCardiMembers.Where(x => x.UserId == userId));

            if (organizationSpent)
            {
                // Account-wide alarm defaults are keyed on the organisation, not the user, so
                // they are shared configuration while anyone else is still in it. States first,
                // the same order MetricAlarmService.DeleteAsync takes: an alarm row removed
                // ahead of its states leaves rows naming an alarm id that no longer resolves,
                // and nothing else would ever collect them — the member cascade only deletes
                // states for a member it is erasing.
                await Step("MetricAlarmStates (account alarms)", _db.MetricAlarmStates
                    .Where(s => _db.MetricAlarms.Any(a =>
                        a.Id == s.MetricAlarmId
                        && a.OrganizationId == user.OrganizationId
                        && a.CardiMemberId == null)));
                await Step("MetricAlarms (account rows)", _db.MetricAlarms
                    .Where(a => a.OrganizationId == user.OrganizationId && a.CardiMemberId == null));

                // Trial and plan records, not a billing ledger: no payment has ever been taken
                // (Stripe is R2, unbuilt), so there is nothing here that UK tax law requires be
                // kept. Revisit when billing ships — an invoice is not a subscription row, and
                // whatever holds one will need an exception of its own.
                await Step("Subscriptions", _db.Subscriptions
                    .Where(s => s.OrganizationId == user.OrganizationId));
            }

            await Step("Users", _db.Users.Where(u => u.Id == userId));

            if (organizationSpent)
                await Step("Organizations", _db.Organizations.Where(o => o.Id == user.OrganizationId));

            await transaction.CommitAsync(rest);
        }
        catch
        {
            await transaction.RollbackAsync(rest);
            throw;
        }

        // CancellationToken.None, deliberately, and this is the one place in the cascade where
        // honouring the token would do harm. Everything above is committed: the Reports rows that
        // named these objects are gone, and so is the user the next sweep would have found them
        // from. A cancellation here does not stop an erasure — it strands a complete identified
        // health export in a bucket with nothing left in the database that knows its name. The
        // objects are few and the deletes are quick, so finishing is strictly better than a tidy
        // shutdown.
        foreach (var objectName in reportObjects)
            await RemoveReportObjectAsync(objectName, orphaned, userId, CancellationToken.None);

        _logger.LogInformation(
            "Account erasure for {UserId} complete. Members erased: {Erased}, released: " +
            "{Released}, tables touched: {Tables}, orphaned objects: {Orphaned}.",
            userId, erased.Count, released.Count, rows.Count, orphaned.Count);

        return new AccountErasureReport(userId, erased, released, rows, orphaned);
    }

    /// <summary>
    /// Whether any caregiver other than the departing one still actively watches this member.
    /// </summary>
    private Task<bool> IsStillWatchedByAnotherAsync(Guid memberId, Guid userId, CancellationToken ct) =>
        _db.UserCardiMembers.AnyAsync(
            l => l.CardiMemberId == memberId && l.UserId != userId && l.IsActive, ct);

    /// <summary>
    /// Splits the members this caregiver is linked to into the ones they were the last active
    /// watcher of, and the ones somebody else still watches.
    /// </summary>
    /// <remarks>
    /// An inactive link is somebody who has already removed the member, so it does not count as
    /// still watching — but an inactive link of the <em>departing</em> caregiver still counts as
    /// a link worth examining: a member they removed last month, whom nobody else ever watched,
    /// is theirs alone and goes with them.
    /// </remarks>
    private async Task<(List<Guid> Erase, List<Guid> Release)> PartitionMembersAsync(
        Guid userId, CancellationToken ct)
    {
        var linked = await _db.UserCardiMembers
            .Where(l => l.UserId == userId)
            .Select(l => l.CardiMemberId)
            .Distinct()
            .ToListAsync(ct);

        var stillWatched = await _db.UserCardiMembers
            .Where(l => linked.Contains(l.CardiMemberId) && l.UserId != userId && l.IsActive)
            .Select(l => l.CardiMemberId)
            .Distinct()
            .ToListAsync(ct);

        var watched = stillWatched.ToHashSet();

        return (linked.Where(id => !watched.Contains(id)).ToList(), stillWatched);
    }

    /// <summary>
    /// Deletes one export object, recording it as orphaned rather than throwing if it will not go
    /// — the rows are already committed, so an exception here would make a completed erasure look
    /// like a failed one.
    /// </summary>
    /// <remarks>
    /// Unlike the member cascade's equivalent, this catches <see cref="OperationCanceledException"/>
    /// too. There, excluding it keeps a cancelled erasure from reading as one that merely left
    /// files behind. Here there is nothing left to be honest to: the rows naming these objects are
    /// already committed away, so a cancellation that escaped would lose the name rather than
    /// report it, and the object would be unfindable rather than merely orphaned. It is logged and
    /// listed like any other failure.
    /// </remarks>
    private async Task RemoveReportObjectAsync(
        string objectName, List<string> orphaned, Guid userId, CancellationToken ct)
    {
        try
        {
            await _reports.DeleteAsync(objectName, ct);
        }
        catch (Exception ex)
        {
            orphaned.Add(objectName);
            _logger.LogWarning(
                ex,
                "Account erasure for {UserId} removed its rows but left export object " +
                "{ObjectName} behind. This needs deleting by hand — the erasure is not complete " +
                "until it is gone.",
                userId, objectName);
        }
    }
}
