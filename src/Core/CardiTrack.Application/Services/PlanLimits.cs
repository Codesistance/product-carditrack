using CardiTrack.Application.Exceptions;
using CardiTrack.Application.Interfaces.Repositories;

namespace CardiTrack.Application.Services;

/// <summary>
/// What a family's plan still has room for.
/// </summary>
/// <remarks>
/// <para>
/// The limits have existed on <c>Subscription</c> since the beginning and nothing has ever checked
/// them, which was harmless while a family could only ever hold one user. Family sharing ends that:
/// without a check, "up to 5 family members" on the pricing page is a sentence with nothing behind
/// it.
/// </para>
/// <para>
/// <strong>A ceiling, never a paywall.</strong> Billing does not exist yet, so nothing here decides
/// whether somebody may use a feature — it decides whether one more row fits. A family with no
/// subscription at all is not blocked, because that state means the trial machinery has not run
/// rather than that the family has run out of room, and refusing them would break onboarding to
/// enforce a limit nobody is being charged for.
/// </para>
/// </remarks>
public static class PlanLimits
{
    /// <summary>
    /// Throws when the family already holds as many people as its plan allows.
    /// </summary>
    /// <remarks>
    /// Counts active memberships, so somebody who left frees their place — the limit is on who is
    /// in the family now, not on how many people have ever been.
    /// </remarks>
    public static async Task RequireRoomForAnotherPersonAsync(
        IUnitOfWork unitOfWork, Guid organizationId, CancellationToken ct = default)
    {
        var subscription = await unitOfWork.Subscriptions.GetByOrganizationIdAsync(organizationId);
        if (subscription is null)
            return;

        var people = (await unitOfWork.UserOrganizations.GetByOrganizationIdAsync(organizationId))
            .Count(m => m.IsActive);

        if (people >= subscription.MaxUsers)
        {
            throw new FamilyRuleException(
                FamilyRuleException.MemberLimitReached,
                $"This family's plan covers {subscription.MaxUsers} " +
                $"{(subscription.MaxUsers == 1 ? "person" : "people")}, and there " +
                $"{(people == 1 ? "is" : "are")} already {people}.");
        }
    }

    /// <summary>
    /// Throws when the family already watches as many CardiMembers as its plan allows.
    /// </summary>
    /// <remarks>
    /// Counts active members only. A removed member is a soft delete that keeps its row, so
    /// counting rows rather than live ones would charge a family for people they stopped watching
    /// months ago.
    /// </remarks>
    public static async Task RequireRoomForAnotherCardiMemberAsync(
        IUnitOfWork unitOfWork, Guid organizationId, CancellationToken ct = default)
    {
        var subscription = await unitOfWork.Subscriptions.GetByOrganizationIdAsync(organizationId);
        if (subscription is null)
            return;

        var watched = (await unitOfWork.CardiMembers.GetByOrganizationIdAsync(organizationId)).Count();

        if (watched >= subscription.MaxCardiMembers)
        {
            throw new FamilyRuleException(
                FamilyRuleException.CardiMemberLimitReached,
                $"This family's plan covers {subscription.MaxCardiMembers} " +
                $"{(subscription.MaxCardiMembers == 1 ? "person" : "people")} to watch, " +
                $"and you're already watching {watched}.");
        }
    }
}
