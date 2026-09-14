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

        foreach (var memberId in toErase)
        {
            ct.ThrowIfCancellationRequested();

            var report = await _members.EraseAsync(memberId, ct);
            erased.Add(memberId);
            rows.AddRange(report.RowsByTable.Select(r => ($"{r.Table} ({memberId})", r.Rows)));
            orphaned.AddRange(report.OrphanedObjects);
        }

        // Read before deleting, as the member cascade does: once the rows are gone nothing
        // remembers which objects they named.
        var reportObjects = await _db.Reports
            .Where(r => r.OwnerUserId == userId && r.ObjectName != null)
            .Select(r => r.ObjectName!)
            .ToListAsync(ct);

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            // Asked before the user row goes, because afterwards the question cannot be asked —
            // and inside the transaction, so somebody joining the household while the cascade
            // runs cannot leave their own organisation deleted underneath them.
            var lastInOrganization = !await _db.Users
                .AnyAsync(u => u.OrganizationId == user.OrganizationId && u.Id != userId, ct);

            async Task Step<T>(string table, IQueryable<T> query) where T : class =>
                rows.Add((table, await query.ExecuteDeleteAsync(ct)));

            // Null, do not delete (runbook row 40). The alert and the answer belong to the
            // member, who may still be being watched by somebody else; only the name of the
            // caregiver who touched them goes.
            rows.Add(("Alerts.AcknowledgedByUserId (nulled)", await _db.Alerts
                .Where(a => a.AcknowledgedByUserId == userId)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.AcknowledgedByUserId, (Guid?)null), ct)));
            rows.Add(("MemberQuestionnaires.AnsweredByUserId (nulled)", await _db.MemberQuestionnaires
                .Where(q => q.AnsweredByUserId == userId)
                .ExecuteUpdateAsync(s => s.SetProperty(q => q.AnsweredByUserId, (Guid?)null), ct)));

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

            if (lastInOrganization)
            {
                // Account-wide alarm defaults are keyed on the organisation, not the user, so
                // they are shared configuration while anyone else is still in it.
                await Step("MetricAlarms (account rows)", _db.MetricAlarms
                    .Where(a => a.OrganizationId == user.OrganizationId && a.CardiMemberId == null));
                await Step("Subscriptions", _db.Subscriptions
                    .Where(s => s.OrganizationId == user.OrganizationId));
            }

            await Step("Users", _db.Users.Where(u => u.Id == userId));

            if (lastInOrganization)
                await Step("Organizations", _db.Organizations.Where(o => o.Id == user.OrganizationId));

            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }

        foreach (var objectName in reportObjects)
            await RemoveReportObjectAsync(objectName, orphaned, userId, ct);

        _logger.LogInformation(
            "Account erasure for {UserId} complete. Members erased: {Erased}, released: " +
            "{Released}, tables touched: {Tables}, orphaned objects: {Orphaned}.",
            userId, erased.Count, toRelease.Count, rows.Count, orphaned.Count);

        return new AccountErasureReport(userId, erased, toRelease, rows, orphaned);
    }

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
    /// like a failed one. Same stance, and the same cancellation exclusion, as the member cascade.
    /// </summary>
    private async Task RemoveReportObjectAsync(
        string objectName, List<string> orphaned, Guid userId, CancellationToken ct)
    {
        try
        {
            await _reports.DeleteAsync(objectName, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
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
