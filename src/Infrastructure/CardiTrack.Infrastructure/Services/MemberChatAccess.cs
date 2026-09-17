using CardiTrack.Application.Interfaces.Services;

namespace CardiTrack.Infrastructure.Services;

/// <summary>
/// Manage-access as a yes/no for chat rungs that already know the member exists.
/// The access service's 404 is non-disclosure for outsiders; inside a conversation
/// it becomes "only the primary caregiver can".
/// </summary>
internal static class MemberChatAccess
{
    public static async Task<bool> CanManageAsync(
        ICardiMemberAccessService access, Guid userId, Guid cardiMemberId, CancellationToken ct)
    {
        try
        {
            await access.RequireManageAccessAsync(userId, cardiMemberId, ct);
            return true;
        }
        catch (KeyNotFoundException)
        {
            return false;
        }
    }
}
