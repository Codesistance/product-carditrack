using CardiTrack.Mobile.Core.Notifications;

namespace CardiTrack.Mobile;

/// <summary>
/// Maps a parsed notification destination onto the Shell route that serves it today.
/// </summary>
/// <remarks>
/// Parsing lives in <see cref="NudgeLinkParser"/> (Mobile.Core, where it is unit-tested); this is
/// only the route table, which cannot leave the MAUI project because it names the pages.
/// </remarks>
public static class DeepLinkRouter
{
    /// <summary>
    /// The Shell route for a link, or null when nothing in this build serves it — including
    /// <see cref="NudgeDestinationKind.TimeZone"/>, which the inbox answers in place rather than
    /// by navigating.
    /// </summary>
    public static string? Resolve(string? deepLink) => Resolve(NudgeLinkParser.Parse(deepLink));

    /// <summary>
    /// The same route table for a destination that has already been parsed — the push tap path
    /// arrives holding one (<c>PushRegistrationCoordinator.DestinationTapped</c>), and parsing it
    /// back into a string only to parse it again would be the second copy of that mapping.
    /// </summary>
    public static string? Resolve(NudgeDestination destination)
    {
        return destination.Kind switch
        {
            NudgeDestinationKind.MemberDevices when destination.CardiMemberId is { } d
                => $"{DeviceManagementPage.Route}?memberId={d}",

            NudgeDestinationKind.MemberEdit when destination.CardiMemberId is { } e
                => $"{EditCardiMemberPage.Route}?memberId={e}",

            // Baseline progress lives on the member detail screen (M1-13) rather than a page of
            // its own, so both destinations land there.
            NudgeDestinationKind.MemberBaseline when destination.CardiMemberId is { } b
                => $"{CardiMemberDetailPage.Route}?memberId={b}",

            NudgeDestinationKind.MemberDetail when destination.CardiMemberId is { } m
                => $"{CardiMemberDetailPage.Route}?memberId={m}",

            NudgeDestinationKind.Settings => AppShell.SettingsRoute,

            _ => null
        };
    }
}
