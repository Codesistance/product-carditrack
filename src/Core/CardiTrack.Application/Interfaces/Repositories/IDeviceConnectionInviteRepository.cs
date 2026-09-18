using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Interfaces.Repositories;

/// <summary>
/// Wearer-side device invitations (<see cref="DeviceConnectionInvite"/>): the caregiver's app
/// creates and watches them, the anonymous wearer endpoints advance them, and the Worker sweeps up
/// the finished ones.
/// </summary>
public interface IDeviceConnectionInviteRepository : IRepository<DeviceConnectionInvite>
{
    /// <summary>
    /// The invite a token names, or null when no invite has that hash. Says nothing about whether
    /// it is live — an expired or already-spent invite comes back here and the caller decides,
    /// because the wearer-facing page has to tell those cases apart to write the right page.
    /// </summary>
    Task<DeviceConnectionInvite?> GetByTokenHashAsync(string tokenHash, CancellationToken ct = default);

    /// <summary>
    /// This member's live invite for this brand — Pending or Opened, and not yet past its expiry —
    /// if they have one. At most one can exist; the partial unique index on the table is what makes
    /// that true when two caregivers tap at the same moment.
    /// </summary>
    Task<DeviceConnectionInvite?> GetLiveAsync(
        Guid cardiMemberId, DeviceType deviceType, DateTime utcNow, CancellationToken ct = default);

    /// <summary>
    /// Moves an invite to a terminal state, in the database, only if it is still in one of
    /// <paramref name="from"/>. Returns whether this call was the one that moved it.
    /// </summary>
    /// <remarks>
    /// A read-then-write would let two racing requests both decide an invite was still open — the
    /// wearer double-tapping Yes, or tapping Yes while the caregiver cancels. The precondition
    /// travels in the UPDATE's WHERE clause so exactly one of them can win, and the loser is told
    /// the invite is no longer live rather than quietly overwriting the winner's outcome.
    /// </remarks>
    Task<bool> TryResolveAsync(
        Guid inviteId,
        IReadOnlyCollection<DeviceInviteStatus> from,
        DeviceInviteStatus to,
        DateTime resolvedAt,
        Guid? deviceConnectionId,
        CancellationToken ct = default);

    /// <summary>
    /// Marks an invite opened, if it is still Pending. A wearer who reloads the page is not an
    /// error, so a false return here means nothing more than "it was already opened".
    /// </summary>
    Task<bool> TryMarkOpenedAsync(Guid inviteId, DateTime openedAt, CancellationToken ct = default);

    /// <summary>
    /// Records which connection a completed invitation produced.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="TryResolveAsync"/> because the two facts become known at different
    /// moments: the invitation is claimed <em>before</em> the connection is written — that ordering
    /// is what lets a revocation stop the write — and the connection's id does not exist until
    /// afterwards. Writes nothing unless the invitation is already Completed, so a late or repeated
    /// call cannot attach a connection to an invitation that was declined or revoked.
    /// </remarks>
    Task RecordConnectionAsync(Guid inviteId, Guid deviceConnectionId, CancellationToken ct = default);

    /// <summary>
    /// Revokes every live invite for this member and brand, returning how many it revoked. Called
    /// when a replacement is created: the caregiver asking for a new code means the old one should
    /// stop working, whether they sent it to the wrong person or simply let it go stale.
    /// </summary>
    Task<int> RevokeLiveAsync(
        Guid cardiMemberId, DeviceType deviceType, DateTime utcNow, CancellationToken ct = default);

    /// <summary>
    /// Invites finished or expired before <paramref name="cutoff"/>, oldest first, capped at
    /// <paramref name="limit"/>. The Worker's retention read.
    /// </summary>
    Task<IReadOnlyList<DeviceConnectionInvite>> GetSweepableAsync(
        DateTime cutoff, int limit, CancellationToken ct = default);
}
