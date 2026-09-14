using CardiTrack.Domain.Entities;

namespace CardiTrack.Application.Interfaces.Repositories;

public interface IUserCardiMemberRepository : IRepository<UserCardiMember>
{
    Task<IEnumerable<UserCardiMember>> GetByUserIdAsync(Guid userId);
    Task<IEnumerable<UserCardiMember>> GetByCardiMemberIdAsync(Guid cardiMemberId);

    /// <summary>
    /// Whether every caregiver actively watching this member has asked for their account to go —
    /// the condition under which collection stops.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The predicate behind "asking to delete your account stops monitoring for anyone it leaves
    /// without a caregiver". <c>DeviceConnectionRepository</c> spells the negation of this inline
    /// at its four sync-scheduling sites; this is the reusable form, for the paths that reach the
    /// sync service without going through the scheduler, and it has to agree with them exactly —
    /// a member that routine sync still collects for, but that no other path will look at, is a
    /// member whose family is told nothing while the readings pile up.
    /// </para>
    /// <para>
    /// <strong>False for a member with no active link at all</strong>, which is not an oversight
    /// and is the half that is easy to get wrong. An ordinary link removal leaves a member nobody
    /// is watching, and that member keeps being collected for: they may be re-linked, and the
    /// readings in the gap are theirs. Only a member who <em>has</em> watchers, all of whom are
    /// leaving, is one the product has been asked to stop collecting for.
    /// </para>
    /// </remarks>
    Task<bool> IsLeftUnwatchedByPendingDeletionAsync(Guid cardiMemberId);
}
