using CardiTrack.Application.Interfaces.Clients;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Domain.Enums;
using CardiTrack.Domain.Entities;
using CardiTrack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CardiTrack.Infrastructure.Services;

/// <summary>
/// The member-scoped erasure cascade, automated from
/// <c>docs/technical/manual_erasure_runbook.md</c>.
/// </summary>
/// <remarks>
/// <para>
/// In Infrastructure and written against the <see cref="DbContext"/> rather than the repositories,
/// which is the deliberate exception rather than a shortcut. A cascade is twenty-six set-based
/// deletes whose whole correctness is their order; expressed through per-aggregate repositories it
/// would be twenty-six new interface methods, each loading rows into memory to delete them, and
/// the order — the part that matters — would be spread across the same number of files.
/// <c>ExecuteDeleteAsync</c> keeps it in one readable sequence that says what it removes and in
/// what order. The port it implements lives in <c>Application</c>, so callers still depend on the
/// contract, not on this.
/// </para>
/// <para>
/// Every step is <c>ExecuteDeleteAsync</c>, which issues a single <c>DELETE … WHERE</c> and
/// bypasses the change tracker. That is what makes a member with two years of minute-grain
/// readings erasable at all: the tracked alternative would load millions of rows to mark them
/// deleted.
/// </para>
/// </remarks>
public class MemberErasureService : IMemberErasureService
{
    private readonly CardiTrackDbContext _db;
    private readonly IProfilePhotoStorage _photos;
    private readonly IReportStorage _reports;
    private readonly IOAuthGrantRevoker _grantRevoker;
    private readonly ILogger<MemberErasureService> _logger;

    public MemberErasureService(
        CardiTrackDbContext db,
        IProfilePhotoStorage photos,
        IReportStorage reports,
        IOAuthGrantRevoker grantRevoker,
        ILogger<MemberErasureService> logger)
    {
        _db = db;
        _photos = photos;
        _reports = reports;
        _grantRevoker = grantRevoker;
        _logger = logger;
    }

    public async Task<MemberErasureReport> EraseAsync(Guid cardiMemberId, CancellationToken ct = default)
    {
        // Read before deleting: these name files outside Postgres, and once the rows are gone
        // nothing remembers which. Collected first, removed after the commit. The photo is the
        // exception — it is found by prefix rather than by name, because the row names only the
        // object it last pointed at (see IProfilePhotoStorage.DeleteAllForMemberAsync).
        var reportObjects = await _db.Reports
            .Where(r => r.CardiMemberIds.Contains(cardiMemberId) && r.ObjectName != null)
            .Select(r => r.ObjectName!)
            .ToListAsync(ct);

        // Before the rows, not after: the runbook's "revoke upstream before deleting" step, which
        // until now was a thing an operator had to remember. Once DeviceConnections is deleted the
        // refresh token is gone and nothing can ever end that grant — the wearer would be left
        // with CardiTrack still listed among the apps that can read their health data, for a
        // member whose every row we have just destroyed. A provider that will not answer cannot
        // stop the erasure, so failures are collected and reported rather than thrown.
        var unrevoked = new List<Guid>();
        foreach (var connection in await _db.DeviceConnections
                     .Where(c => c.CardiMemberId == cardiMemberId)
                     .ToListAsync(ct))
        {
            if (!await _grantRevoker.TryRevokeAsync(connection, ct))
                unrevoked.Add(connection.Id);
        }

        if (unrevoked.Count > 0)
        {
            _logger.LogWarning(
                "Erasure of CardiMember {CardiMemberId} could not confirm revocation of "
                + "{Count} device grant(s): {Connections}. They are still live at the provider "
                + "and the tokens that could have ended them are about to be deleted — revoke "
                + "from the wearer's provider account by hand.",
                cardiMemberId, unrevoked.Count, string.Join(", ", unrevoked));
        }

        var rows = new List<(string Table, int Rows)>();

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            // Before every delete below, and that position is the whole point of it. An AI
            // generator reads the member, spends minutes in a model call, then writes a row naming
            // them — and no foreign key stops that row landing after this cascade has passed the
            // table it goes in. IMemberWriteGuard holds FOR KEY SHARE on this row while it writes;
            // FOR UPDATE here conflicts with it, which leaves exactly two orderings and makes both
            // safe. Erasure first: the generator's lock waits here, then finds no row and writes
            // nothing. Generator first: this line waits for its commit, and the deletes below then
            // sweep the row it wrote as ordinary cascade work. Taken after the deletes it would
            // guarantee nothing; taken as a plain read it would guarantee nothing either.
            //
            // No row to lock is not a failure. A member already erased — a retried sweep, a
            // half-finished manual run — still has orphans worth collecting, and the cascade below
            // is what collects them. That is today's behaviour and it stays.
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"""SELECT 1 FROM "CardiMembers" WHERE "Id" = {cardiMemberId} FOR UPDATE""", ct);

            // The runbook's order, and it is the order for a reason: children before parents, and
            // the two tables that are easy to miss (DeviceActivityLogs before ActivityLogs, the
            // raw rows before the merged view) kept adjacent so neither is dropped by accident.
            async Task Step<T>(string table, IQueryable<T> query) where T : class =>
                rows.Add((table, await query.ExecuteDeleteAsync(ct)));

            await Step("NotificationDeliveries", _db.NotificationDeliveries.Where(x => x.CardiMemberId == cardiMemberId));
            await Step("NotificationMutes", _db.NotificationMutes.Where(x => x.CardiMemberId == cardiMemberId));
            await Step("Notifications", _db.Notifications.Where(x => x.CardiMemberId == cardiMemberId));
            await Step("AlertPreferences", _db.AlertPreferences.Where(x => x.CardiMemberId == cardiMemberId));
            await Step("Alerts", _db.Alerts.Where(x => x.CardiMemberId == cardiMemberId));
            await Step("PatternBaselines", _db.PatternBaselines.Where(x => x.CardiMemberId == cardiMemberId));
            await Step("RealtimeAssessments", _db.RealtimeAssessments.Where(x => x.CardiMemberId == cardiMemberId));
            await Step("DigestEntries", _db.DigestEntries.Where(x => x.CardiMemberId == cardiMemberId));
            await Step("EnvironmentalReadings", _db.EnvironmentalReadings.Where(x => x.CardiMemberId == cardiMemberId));
            await Step("GranularMetricHours", _db.GranularMetricHours.Where(x => x.CardiMemberId == cardiMemberId));
            await Step("MetricRollupsHourly", _db.MetricRollupsHourly.Where(x => x.CardiMemberId == cardiMemberId));
            await Step("DeviceActivityLogs", _db.DeviceActivityLogs.Where(x => x.CardiMemberId == cardiMemberId));
            await Step("ActivityLogs", _db.ActivityLogs.Where(x => x.CardiMemberId == cardiMemberId));
            await Step("MemberQuestionnaires", _db.MemberQuestionnaires.Where(x => x.CardiMemberId == cardiMemberId));

            // Turns and usages cascade from the session by configuration, but they are deleted
            // explicitly first so the counts in the report are real rather than inferred.
            await Step("MemberChatTurnUsages", _db.MemberChatTurnUsages
                .Where(u => _db.MemberChatSessions
                    .Any(s => s.CardiMemberId == cardiMemberId
                              && _db.MemberChatTurns.Any(t => t.SessionId == s.Id && t.Id == u.TurnId))));
            await Step("MemberChatTurns", _db.MemberChatTurns
                .Where(t => _db.MemberChatSessions.Any(s => s.Id == t.SessionId && s.CardiMemberId == cardiMemberId)));
            await Step("MemberChatSessions", _db.MemberChatSessions.Where(x => x.CardiMemberId == cardiMemberId));

            await Step("MemberAdvises", _db.Set<MemberAdvise>().Where(x => x.CardiMemberId == cardiMemberId));
            await Step("MemberAdviseObservations", _db.MemberAdviseObservations.Where(x => x.CardiMemberId == cardiMemberId));
            await Step("MemberInsights", _db.Set<MemberInsight>().Where(x => x.CardiMemberId == cardiMemberId));
            await Step("MetricAlarmStates", _db.MetricAlarmStates.Where(x => x.CardiMemberId == cardiMemberId));
            await Step("MetricAlarms", _db.MetricAlarms.Where(x => x.CardiMemberId == cardiMemberId));
            await Step("MemberStatusLines", _db.MemberStatusLines.Where(x => x.CardiMemberId == cardiMemberId));
            await Step("MemberAiHolds", _db.MemberAiHolds.Where(x => x.CardiMemberId == cardiMemberId));
            await Step("DeviceHistoryRepulls", _db.DeviceHistoryRepulls.Where(x => x.CardiMemberId == cardiMemberId));

            // Array columns, not foreign keys: a consent or a report naming this member among
            // others still described their health data, so the row goes with them.
            await Step("ExportConsents", _db.ExportConsents.Where(x => x.CardiMemberIds.Contains(cardiMemberId)));
            await Step("Reports", _db.Reports.Where(x => x.CardiMemberIds.Contains(cardiMemberId)));

            await Step("DeviceConnections", _db.DeviceConnections.Where(x => x.CardiMemberId == cardiMemberId));
            await Step("CardiMemberCreationKeys", _db.CardiMemberCreationKeys.Where(x => x.CardiMemberId == cardiMemberId));
            await Step("UserCardiMembers", _db.UserCardiMembers.Where(x => x.CardiMemberId == cardiMemberId));
            await Step("CardiMembers", _db.CardiMembers.Where(x => x.Id == cardiMemberId));

            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }

        var orphaned = new List<string>();
        var photoRemoved = false;

        // Every object under the member's prefix, not just the one the row named: an upload
        // interrupted between writing the object and saving its name leaves a face photo nothing
        // points at, and erasure is exactly when that must not survive.
        // CancellationToken.None from here down, deliberately. Everything above is committed: the
        // CardiMembers row is gone, and so are the Reports rows that named these objects. A
        // cancellation at this point does not stop an erasure — it loses the only remaining record
        // of which objects are left, turning a reportable orphan into an unfindable one. The
        // objects are few and the deletes are quick, so finishing beats a tidy shutdown.
        try
        {
            var leftBehind = await _photos.DeleteAllForMemberAsync(cardiMemberId, CancellationToken.None);
            orphaned.AddRange(leftBehind);
            photoRemoved = leftBehind.Count == 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Erasure of CardiMember {CardiMemberId} removed its rows but could not clear its "
                + "photo prefix. Those objects need deleting by hand.",
                cardiMemberId);
            orphaned.Add($"members/{cardiMemberId}/");
        }

        foreach (var objectName in reportObjects)
            await RemoveObjectAsync(
                objectName, o => _reports.DeleteAsync(o, CancellationToken.None), orphaned, cardiMemberId);

        return new MemberErasureReport(cardiMemberId, rows, photoRemoved, unrevoked, orphaned);
    }

    /// <summary>
    /// Deletes one storage object, recording it as orphaned rather than throwing if it will not go.
    /// </summary>
    /// <remarks>
    /// Throwing here would be the wrong failure: the rows are already committed, so the caller
    /// cannot undo the erasure, and turning a leftover file into an exception would make a
    /// completed erasure look like a failed one. The file is named in the report and logged at
    /// warning, which is what a manual clean-up needs.
    ///
    /// That includes cancellation. It is tempting to let a cancelled run escape so it does not
    /// read as a finished one, and this method used to — but the rows are gone by the time this
    /// runs, so an escaping cancellation loses the object's name rather than reporting it, and a
    /// health export nothing points at is worse than one the report names.
    /// </remarks>
    private async Task<bool> RemoveObjectAsync(
        string objectName, Func<string, Task> delete, List<string> orphaned, Guid cardiMemberId)
    {
        try
        {
            await delete(objectName);
            return true;
        }
        catch (Exception ex)
        {
            orphaned.Add(objectName);
            _logger.LogWarning(
                ex,
                "Erasure of CardiMember {CardiMemberId} removed its rows but left storage object "
                + "{ObjectName} behind. This needs deleting by hand — the erasure is not complete "
                + "until it is gone.",
                cardiMemberId, objectName);
            return false;
        }
    }
}
