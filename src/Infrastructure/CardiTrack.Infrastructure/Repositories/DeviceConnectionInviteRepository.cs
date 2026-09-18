using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CardiTrack.Infrastructure.Repositories;

public class DeviceConnectionInviteRepository
    : Repository<DeviceConnectionInvite>, IDeviceConnectionInviteRepository
{
    /// <summary>
    /// The statuses an invite can still be completed from.
    /// </summary>
    /// <remarks>
    /// An array with <c>Contains</c>, the same shape the sibling repositories use, because that
    /// translates to a SQL <c>IN</c>. A helper method reads better and does not translate at all: EF
    /// cannot see inside it, so the query throws at runtime rather than failing to compile. This list
    /// and the partial index filter in <c>DeviceConnectionInviteConfiguration</c> must name the same
    /// statuses.
    /// </remarks>
    private static readonly DeviceInviteStatus[] LiveStatuses =
        [DeviceInviteStatus.Pending, DeviceInviteStatus.Opened];

    public DeviceConnectionInviteRepository(CardiTrackDbContext context) : base(context)
    {
    }

    /// <summary>
    /// Reads one invite, always from the database.
    /// </summary>
    /// <remarks>
    /// Overridden away from the base <c>FindAsync</c>, which answers from the change tracker when it
    /// can, because every status transition on this entity is an <c>ExecuteUpdateAsync</c> and those
    /// deliberately do not tell the tracker anything. A caller that created or read an invite
    /// earlier in the same scope, resolved it, then read it back would otherwise be handed the
    /// pre-transition copy — and the shape that takes in practice is a caregiver cancelling an
    /// invitation and being shown the status it had before they cancelled it.
    /// <para>
    /// Safe because nothing in this feature mutates a tracked invite: reads report, and writes go
    /// through the conditional updates below.
    /// </para>
    /// </remarks>
    public override async Task<DeviceConnectionInvite?> GetByIdAsync(Guid id) =>
        await _dbSet.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id);

    public async Task<DeviceConnectionInvite?> GetByTokenHashAsync(
        string tokenHash, CancellationToken ct = default)
    {
        return await _dbSet
            .AsNoTracking()
            .FirstOrDefaultAsync(i => i.TokenHash == tokenHash, ct);
    }

    public async Task<DeviceConnectionInvite?> GetLiveAsync(
        Guid cardiMemberId, DeviceType deviceType, DateTime utcNow, CancellationToken ct = default)
    {
        return await _dbSet
            .AsNoTracking()
            .Where(i => i.CardiMemberId == cardiMemberId
                        && i.DeviceType == deviceType
                        && LiveStatuses.Contains(i.Status)
                        && i.ExpiresAt > utcNow)
            .OrderByDescending(i => i.CreatedDate)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<bool> TryResolveAsync(
        Guid inviteId,
        IReadOnlyCollection<DeviceInviteStatus> from,
        DeviceInviteStatus to,
        DateTime resolvedAt,
        Guid? deviceConnectionId,
        CancellationToken ct = default)
    {
        var allowed = from.ToArray();

        // The precondition rides in the WHERE clause rather than being checked first in C#: two
        // requests can otherwise both read "still open" and both write their own outcome, and the
        // second silently overwrites the first. Here exactly one UPDATE matches a row.
        var affected = await _dbSet
            .Where(i => i.Id == inviteId && allowed.Contains(i.Status))
            .ExecuteUpdateAsync(set => set
                .SetProperty(i => i.Status, to)
                .SetProperty(i => i.ResolvedAt, resolvedAt)
                .SetProperty(i => i.UpdatedDate, resolvedAt)
                .SetProperty(i => i.DeviceConnectionId,
                    i => deviceConnectionId ?? i.DeviceConnectionId),
                ct);

        return affected > 0;
    }

    public async Task<bool> TryMarkOpenedAsync(
        Guid inviteId, DateTime openedAt, CancellationToken ct = default)
    {
        // Only from Pending, so a reload does not keep moving the timestamp forward: "when did they
        // first look at this" is the question the caregiver's screen is really asking, and the
        // answer should not change every time the wearer pulls to refresh.
        var affected = await _dbSet
            .Where(i => i.Id == inviteId && i.Status == DeviceInviteStatus.Pending)
            .ExecuteUpdateAsync(set => set
                .SetProperty(i => i.Status, DeviceInviteStatus.Opened)
                .SetProperty(i => i.OpenedAt, openedAt)
                .SetProperty(i => i.UpdatedDate, openedAt),
                ct);

        return affected > 0;
    }

    public async Task<int> RevokeLiveAsync(
        Guid cardiMemberId, DeviceType deviceType, DateTime utcNow, CancellationToken ct = default)
    {
        // Expired rows are swept in with the live ones on purpose. They no longer work, but they
        // still occupy the partial unique index until something moves them out of a live status,
        // and an expired invite nobody has cleared must not be what stops a caregiver asking for a
        // new one.
        return await _dbSet
            .Where(i => i.CardiMemberId == cardiMemberId
                        && i.DeviceType == deviceType
                        && LiveStatuses.Contains(i.Status))
            .ExecuteUpdateAsync(set => set
                .SetProperty(i => i.Status, DeviceInviteStatus.Revoked)
                .SetProperty(i => i.ResolvedAt, utcNow)
                .SetProperty(i => i.UpdatedDate, utcNow),
                ct);
    }

    public async Task<IReadOnlyList<DeviceConnectionInvite>> GetSweepableAsync(
        DateTime cutoff, int limit, CancellationToken ct = default)
    {
        // Two ways an invite stops mattering: somebody finished with it, or it timed out with
        // nobody touching it. Both are aged from the moment they happened, so an invitation sent
        // and ignored is retained no longer than one that was used.
        //
        // Ordered by that same moment rather than by expiry alone. Expiry is set from the channel's
        // lifetime, so ordering on it would work through every QR invite before any link invite of
        // the same age — and when a backlog exceeds one batch, that bias decides whose rows wait
        // another day.
        return await _dbSet
            .Where(i => (i.ResolvedAt != null && i.ResolvedAt < cutoff)
                        || (i.ResolvedAt == null && i.ExpiresAt < cutoff))
            .OrderBy(i => i.ResolvedAt ?? i.ExpiresAt)
            .Take(limit)
            .ToListAsync(ct);
    }
}
