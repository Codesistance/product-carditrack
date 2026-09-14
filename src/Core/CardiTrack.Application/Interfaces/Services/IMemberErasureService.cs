namespace CardiTrack.Application.Interfaces.Services;

/// <summary>
/// What one member's erasure removed, table by table, in the order it was removed.
/// </summary>
/// <remarks>
/// Returned rather than logged because the runbook this replaces asks the operator to record a
/// count per table in the verification record, and an automated erasure owes the same evidence.
/// A caller that cannot say what it deleted cannot prove it deleted everything.
/// </remarks>
/// <param name="CardiMemberId">The member erased.</param>
/// <param name="RowsByTable">Rows removed per table, in cascade order. Zero is a normal result.</param>
/// <param name="PhotoObjectRemoved">
/// Whether a profile-photo object was deleted. False when the member had no photo, and also when
/// the object could not be removed — see <see cref="OrphanedObjects"/>.
/// </param>
/// <param name="OrphanedObjects">
/// Storage objects the database rows named but which could not be deleted. Empty is the expected
/// result. Non-empty means the rows are gone and these files are not, which is a manual job and
/// must not be silent: the erasure promise covers the files too.
/// </param>
public sealed record MemberErasureReport(
    Guid CardiMemberId,
    IReadOnlyList<(string Table, int Rows)> RowsByTable,
    bool PhotoObjectRemoved,
    IReadOnlyList<string> OrphanedObjects)
{
    public int TotalRows => RowsByTable.Sum(r => r.Rows);
}

/// <summary>
/// Hard-erases one CardiMember and every row that describes them — with one deliberate
/// exception.
/// </summary>
/// <remarks>
/// <para>
/// The automated form of the member-scoped table in
/// <c>docs/technical/manual_erasure_runbook.md</c>, which is the interim procedure this is meant
/// to replace. The order is the runbook's order, and it is load-bearing: it exists so that no
/// delete trips a foreign key it should have outlived.
/// </para>
/// <para>
/// <strong>`AuditLogs` are kept.</strong> The runbook retains them on purpose: they are the
/// compliance record of who read this member's health data, and destroying that record is not
/// what an erasure request asks for. So a successful report is <em>not</em> proof that no
/// member-linked row exists anywhere — it is proof that every row this contract covers is
/// gone. A caller that needs the stronger claim does not have it.
/// </para>
/// <para>
/// <strong>Erasure, not removal.</strong> <c>ICardiMemberService.RemoveAsync</c> soft-deletes —
/// the member stops being monitored and their rows stay. This is the other thing: the rows go.
/// Nothing calls this on a caregiver tapping "remove"; it is for a verified erasure request and
/// for the account-closure path built on top of it.
/// </para>
/// </remarks>
public interface IMemberErasureService
{
    /// <summary>
    /// Erases the member's rows in one transaction, then removes the storage objects those rows
    /// named. Returns what was removed.
    /// </summary>
    /// <remarks>
    /// Objects are deleted <em>after</em> the commit, and deliberately: a blob delete cannot join
    /// a database transaction, so doing it first would risk destroying a file belonging to rows
    /// that then survive a rollback. The cost is the opposite failure — rows gone, file left —
    /// which is recoverable and is reported rather than swallowed.
    /// </remarks>
    Task<MemberErasureReport> EraseAsync(Guid cardiMemberId, CancellationToken ct = default);
}
