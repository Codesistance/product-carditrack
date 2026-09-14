using CardiTrack.Domain.Entities;

namespace CardiTrack.Application.Interfaces.Repositories;

public interface IUserCardiMemberRepository : IRepository<UserCardiMember>
{
    Task<IEnumerable<UserCardiMember>> GetByUserIdAsync(Guid userId);
    Task<IEnumerable<UserCardiMember>> GetByCardiMemberIdAsync(Guid cardiMemberId);

    /// <summary>
    /// Whether anyone is still watching this member who has not asked for their account to go —
    /// an active link to a user with no outstanding deletion request.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The predicate behind "asking to delete your account stops monitoring for anyone it leaves
    /// without a caregiver". <c>DeviceConnectionRepository</c> spells the same condition inline at
    /// its four sync-scheduling sites; this is the reusable form, for the paths that reach the
    /// sync service without going through the scheduler.
    /// </para>
    /// <para>
    /// False for a member nobody is linked to at all, which is the same answer for the same
    /// reason: there is no one to tell, so there is nothing to collect for.
    /// </para>
    /// </remarks>
    Task<bool> HasWatcherNotAwaitingDeletionAsync(Guid cardiMemberId);
}
