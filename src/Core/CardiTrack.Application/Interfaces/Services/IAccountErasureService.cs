namespace CardiTrack.Application.Interfaces.Services;

/// <summary>
/// What closing one account removed.
/// </summary>
/// <param name="UserId">The caregiver whose account was closed.</param>
/// <param name="MembersErased">
/// Members erased outright — the ones this caregiver was the last active link to.
/// </param>
/// <param name="MembersReleased">
/// Members left alone because someone else still watches them. Their link to <em>this</em>
/// caregiver is gone; the member and their readings are not, and must not be.
/// </param>
/// <param name="RowsByTable">Account-scoped rows removed per table, in the order removed.</param>
/// <param name="OrphanedObjects">
/// Storage objects the rows named that could not be deleted. Empty is the expected result;
/// anything here is a manual job and the erasure is not complete until it is done.
/// </param>
public sealed record AccountErasureReport(
    Guid UserId,
    IReadOnlyList<Guid> MembersErased,
    IReadOnlyList<Guid> MembersReleased,
    IReadOnlyList<(string Table, int Rows)> RowsByTable,
    IReadOnlyList<string> OrphanedObjects);

/// <summary>
/// Closes one caregiver's account: erases the members only they watched, releases the ones other
/// people still watch, then removes what the account itself owns.
/// </summary>
/// <remarks>
/// <para>
/// The automated form of the account-scoped table in
/// <c>docs/technical/manual_erasure_runbook.md</c>, and it delegates the member half to
/// <see cref="IMemberErasureService"/> rather than repeating it — one cascade, one order, one
/// place to keep in step with the runbook.
/// </para>
/// <para>
/// <strong>The release rule is the part worth reading twice.</strong> A member watched by two
/// caregivers does not belong to either of them. When one leaves, that member keeps their
/// readings, their alerts and their monitoring; only the departing caregiver's link goes. Erasing
/// them because one person asked to be forgotten would take a second family's record with it.
/// </para>
/// <para>
/// <strong><c>AuditLogs</c> are kept</strong>, as they are for a member erasure: they are the
/// record of who read health data, which an erasure request does not ask to destroy. Billing rows
/// are kept too, for the six years UK tax law requires — see the policy's §5. So a successful
/// report means every row this contract covers is gone, not that no row anywhere names this
/// person.
/// </para>
/// </remarks>
public interface IAccountErasureService
{
    /// <summary>
    /// Erases the account and everything it solely owns. Returns what went and what was kept.
    /// </summary>
    Task<AccountErasureReport> EraseAsync(Guid userId, CancellationToken ct = default);
}
