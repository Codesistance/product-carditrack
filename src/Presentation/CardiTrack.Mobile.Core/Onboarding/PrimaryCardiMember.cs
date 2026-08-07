using CardiTrack.Application.DTOs.Responses;

namespace CardiTrack.Mobile.Core.Onboarding;

/// <summary>
/// Which CardiMember the app acts on when it hasn't been told. One rule in one place —
/// the dashboard, the post-login resume and the device-setup launcher must not drift
/// apart on which member they mean.
/// </summary>
public static class PrimaryCardiMember
{
    /// <summary>
    /// The remembered member if it's still in the list, otherwise the first active one,
    /// otherwise simply the first. Null only when there are no members at all.
    /// </summary>
    public static CardiMemberResponse? From(IReadOnlyList<CardiMemberResponse>? members, Guid? preferredId = null)
    {
        if (members is null || members.Count == 0)
            return null;

        if (preferredId is { } id)
        {
            var remembered = members.FirstOrDefault(m => m.Id == id);
            if (remembered is not null)
                return remembered;
        }

        return members.FirstOrDefault(m => m.IsActive) ?? members[0];
    }
}
