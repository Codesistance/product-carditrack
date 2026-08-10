using CardiTrack.Application.Interfaces.Repositories;

namespace CardiTrack.Infrastructure.Services;

/// <summary>
/// Resolves the timezone a member's local clock is anchored to: the earliest-linked active
/// caregiver's <c>User.TimeZoneId</c> — deterministic, and the member entity itself carries no
/// timezone. Extracted from the digest generator when inactivity detection became the second
/// consumer of "what time is it for this member", so the two can never drift: a digest and a
/// waking-hours check disagreeing about a member's morning would be a genuinely confusing bug.
/// <para>
/// Families spread across timezones get one anchor clock (a per-reader refinement is future
/// work); a member with no resolvable zone anchors to UTC so they are never silently skipped.
/// </para>
/// </summary>
internal static class MemberAnchorTimeZone
{
    internal static async Task<TimeZoneInfo> ResolveAsync(IUnitOfWork unitOfWork, Guid cardiMemberId)
    {
        var links = (await unitOfWork.UserCardiMembers.GetByCardiMemberIdAsync(cardiMemberId))
            .Where(l => l.IsActive)
            .OrderBy(l => l.CreatedDate)
            .ThenBy(l => l.UserId);

        foreach (var link in links)
        {
            var user = await unitOfWork.Users.GetByIdAsync(link.UserId);
            if (string.IsNullOrWhiteSpace(user?.TimeZoneId))
                continue;

            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(user.TimeZoneId);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        return TimeZoneInfo.Utc;
    }
}
