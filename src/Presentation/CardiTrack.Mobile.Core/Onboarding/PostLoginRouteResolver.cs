using CardiTrack.Application.DTOs.Responses;

namespace CardiTrack.Mobile.Core.Onboarding;

public enum PostLoginDestination
{
    /// <summary>No server-side user record yet.</summary>
    AccountSetup,

    /// <summary>Signed up, but no CardiMember — the M1-04 wizard as the app root.</summary>
    AddCardiMember,

    /// <summary>Everything the shell needs is in place.</summary>
    Dashboard,
}

/// <param name="ResumeDeviceSetup">
/// Launch the device leg of the wizard over the destination. Only ever set alongside
/// <see cref="PostLoginDestination.Dashboard"/> — the other destinations lead into the
/// device step on their own.
/// </param>
public readonly record struct PostLoginRoute(PostLoginDestination Destination, bool ResumeDeviceSetup);

/// <summary>
/// Where a launch with a valid session should land, given what onboarding reports. Kept
/// apart from the pages it selects so the table can be reasoned about — and tested —
/// without a window to swap roots on.
/// </summary>
public static class PostLoginRouteResolver
{
    public static PostLoginRoute Resolve(OnboardingStatusResponse? status, bool deviceSetupDismissed)
    {
        if (status is null || !status.HasUserAccount)
            return new PostLoginRoute(PostLoginDestination.AccountSetup, false);

        // A guest (D-12) has no family of their own and nobody to watch until an admin lets them
        // in: the add-member wizard would start a family they chose not to start, so they land in
        // the shell, where the Family tab shows the ask they are waiting on. A caregiver who
        // started a family and has not yet added anyone still gets the wizard, as before.
        if (!status.HasCardiMember)
        {
            return status.HasOrganization
                ? new PostLoginRoute(PostLoginDestination.AddCardiMember, false)
                : new PostLoginRoute(PostLoginDestination.Dashboard, false);
        }

        // A member without a device is the resumable case: land on the dashboard and offer
        // the device leg over it, unless the user has already waved it away once.
        return new PostLoginRoute(
            PostLoginDestination.Dashboard,
            !status.HasDeviceConnected && !deviceSetupDismissed);
    }
}
