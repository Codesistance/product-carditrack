namespace CardiTrack.Application.Interfaces.Services;

/// <summary>
/// Serialises the operations that change a family's shape — who is in it, who runs it, and
/// whether there is room for one more.
/// </summary>
/// <remarks>
/// <para>
/// Every one of those is a read followed by a write: count the members and then add one, find the
/// admin and then move the role, see a free seat and then take it. Each was written when a family
/// held one person, where a second caller was not a case that existed. With several caregivers it
/// is an ordinary Tuesday — two admins answering the same join request, an invitation redeemed
/// while a member is being removed — and read-then-write gives the wrong answer every time both
/// arrive inside the same moment.
/// </para>
/// <para>
/// The lock is <c>FOR UPDATE</c> on the <c>Organizations</c> row: the family itself, which always
/// exists, and which the subscription and every membership hang off. Held for the lifetime of the
/// caller's transaction, so the count and the insert it justifies are one indivisible step rather
/// than two hopeful ones.
/// </para>
/// <para>
/// This is the same tool <see cref="IMemberWriteGuard"/> uses for the erasure race, chosen for the
/// same reason: the schema keeps Guid references without foreign keys, so a row lock is the only
/// place a cross-row invariant can actually be enforced. A second locking idiom would mean two
/// things to reason about when a deadlock appears, so there is one.
/// </para>
/// <para>
/// <strong>Lock ordering.</strong> Callers that need both guards take this one first, then the
/// member guard — family before member, outer before inner. Deadlock-freedom depends on that
/// order, which is why it is written down rather than left to each call site.
/// </para>
/// </remarks>
public interface IFamilyWriteGuard
{
    /// <summary>
    /// Holds this family against concurrent shape changes until the caller's transaction ends.
    /// Returns false when no such family exists, which the caller should treat as "not found"
    /// rather than as a lock it holds.
    /// </summary>
    Task<bool> HoldAsync(Guid organizationId, CancellationToken ct = default);

    /// <summary>
    /// Holds one account against a concurrent decision about which family is its home, until the
    /// caller's transaction ends.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Keyed on the account rather than the family because at the moment it matters there is no
    /// family yet: a guest adding their first CardiMember has one created for them, and two such
    /// requests arriving together would both find <c>OrganizationId</c> null and both create one.
    /// One of those families then has a trial, a subscription and a membership, and nothing
    /// pointing at it.
    /// </para>
    /// <para>
    /// <strong>Lock ordering.</strong> Nothing currently takes this and
    /// <see cref="HoldAsync(Guid, CancellationToken)"/> together. Anything that starts to must
    /// take the account first, then the family — a guest's account has no family to lock at the
    /// point this is needed, so the account is necessarily the outer one.
    /// </para>
    /// </remarks>
    Task<bool> HoldAccountAsync(Guid userId, CancellationToken ct = default);
}
