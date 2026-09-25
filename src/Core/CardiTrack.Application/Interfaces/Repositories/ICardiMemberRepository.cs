using CardiTrack.Domain.Entities;

namespace CardiTrack.Application.Interfaces.Repositories;

public interface ICardiMemberRepository : IRepository<CardiMember>
{
    Task<IEnumerable<CardiMember>> GetByOrganizationIdAsync(Guid organizationId);

    /// <summary>
    /// Takes <c>FOR NO KEY UPDATE</c> on the member's row for the rest of the open transaction.
    /// False when there is no row — it never existed, or an erasure this call waited on has just
    /// committed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For writes that read the member and then write back something derived from what they read —
    /// the medical-information ledger and the notes summary it keeps on the member. Two of those
    /// running at once each see the other's change missing and the last save wins; this lock makes
    /// the second wait for the first and then read what the first committed. It must be the first
    /// statement of the transaction, before the member is loaded: EF keeps the first copy of an
    /// entity it loads, so a member read before the lock stays the stale one.
    /// </para>
    /// <para>
    /// Also erasure-safe, the way <c>IMemberWriteGuard</c> is: <c>NO KEY UPDATE</c> conflicts with
    /// the <c>FOR UPDATE</c> erasure takes as its first statement, so a ledger write either finds the
    /// member gone or finishes before erasure sweeps its rows. Unlike the guard's <c>KEY SHARE</c> it
    /// also conflicts with itself, which is the point. AI writers holding <c>KEY SHARE</c> never wait
    /// on it.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">No transaction is open.</exception>
    Task<bool> LockForUpdateAsync(Guid cardiMemberId, CancellationToken ct = default);
    Task<CardiMember?> GetWithRelationshipsAsync(Guid id);

    /// <summary>
    /// Ids of active members with at least one activity log on or after <paramref name="since"/>.
    /// Ids rather than entities because the caller processes members one scope at a time, and
    /// filtered rather than "all active" so dormant records are not rescanned on every run.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetActiveIdsWithActivitySinceAsync(DateOnly since);

    /// <summary>
    /// As <see cref="GetActiveIdsWithActivitySinceAsync(DateOnly)"/>, restricted to members of the
    /// given organizations. For a pass whose work is defined per organization — the alarm engine
    /// only evaluates members whose organization has an alarm, so walking the rest of the estate to
    /// discard them one by one is pure cost.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetActiveIdsWithActivitySinceAsync(
        DateOnly since, IReadOnlyCollection<Guid> organizationIds);

    /// <summary>
    /// Ids of active members who have explicitly granted
    /// <see cref="Domain.Entities.CardiMember.EnvironmentalContextConsentGranted"/>. The sole
    /// candidate filter for the environmental-enrichment pass — a member absent from this list
    /// is never looked at by that pass, full stop.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetActiveIdsWithEnvironmentalConsentAsync();

    /// <summary>
    /// Every <see cref="Domain.Entities.CardiMember.PhotoObjectName"/> carried by an active
    /// member — the reference set <c>OrphanedPhotoCleanupWorker</c> diffs the photo bucket
    /// against. Names rather than entities: the sweep needs membership tests, nothing else.
    /// </summary>
    Task<IReadOnlyList<string>> GetActivePhotoObjectNamesAsync();

    /// <summary>
    /// Soft-deleted members that still carry a <c>PhotoObjectName</c>. Normally empty — the
    /// removal path clears the column before it saves — so a hit is a crashed removal whose
    /// photo outlived the membership; the cleanup worker deletes the blob and nulls the column.
    /// Entities rather than ids because the caller updates the row it was handed.
    /// </summary>
    Task<IReadOnlyList<CardiMember>> GetInactiveWithPhotoAsync();
}
