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
/// <param name="UnrevokedGrants">
/// Device connections whose grant could not be confirmed revoked before the row was deleted —
/// aggregated from the member erasures this closure performed. Same meaning, and the same manual
/// job: see <see cref="MemberErasureReport"/>.
/// </param>
/// <param name="OrphanedObjects">
/// Storage objects the rows named that could not be deleted. Empty is the expected result;
/// anything here is a manual job and the erasure is not complete until it is done.
/// </param>
/// <param name="DuplicatesElsewhere">
/// Records for the same wearer, in <em>other</em> families, that this erasure did not touch.
/// </param>
/// <param name="UncorrelatedMembers">
/// Members erased here whose duplicates could not be looked for at all, because no device was ever
/// connected to them and so nothing identifies the person behind the record.
/// </param>
public sealed record AccountErasureReport(
    Guid UserId,
    IReadOnlyList<Guid> MembersErased,
    IReadOnlyList<Guid> MembersReleased,
    IReadOnlyList<(string Table, int Rows)> RowsByTable,
    IReadOnlyList<Guid> UnrevokedGrants,
    IReadOnlyList<string> OrphanedObjects,
    IReadOnlyList<Guid> DuplicatesElsewhere,
    IReadOnlyList<Guid> UncorrelatedMembers);

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
/// record of who read health data, which an erasure request does not ask to destroy. So a
/// successful report means every row this contract covers is gone, not that no row anywhere names
/// this person.
/// </para>
/// <para>
/// <strong>The subscription row goes.</strong> The policy's §5 keeps billing records for the six
/// years UK tax law requires, but there are none: Stripe is R2 and unbuilt, no payment has ever
/// been taken, and a <c>Subscription</c> row is a trial and plan record rather than a ledger
/// entry. When billing ships, whatever holds an invoice will need an exception here — and this
/// paragraph is the reminder that it does not have one yet.
/// </para>
/// </remarks>
public interface IAccountErasureService
{
    /// <summary>
    /// Erases the account and everything it solely owns. Returns what went and what was kept.
    /// </summary>
    Task<AccountErasureReport> EraseAsync(Guid userId, CancellationToken ct = default);
}
