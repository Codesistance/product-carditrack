using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Core.Onboarding;

namespace CardiTrack.UnitTests.Mobile;

public class PostLoginRouteResolverTests
{
    private static OnboardingStatusResponse Status(bool account, bool member, bool device) => new()
    {
        HasUserAccount = account,
        HasCardiMember = member,
        HasDeviceConnected = device,
    };

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public void NoUserRecord_GoesToAccountSetup(bool member, bool device)
    {
        var route = PostLoginRouteResolver.Resolve(Status(account: false, member, device), deviceSetupDismissed: false);

        Assert.Equal(PostLoginDestination.AccountSetup, route.Destination);
        Assert.False(route.ResumeDeviceSetup);
    }

    [Fact]
    public void NoCardiMember_GoesToTheAddMemberWizard()
    {
        var route = PostLoginRouteResolver.Resolve(
            Status(account: true, member: false, device: false), deviceSetupDismissed: false);

        Assert.Equal(PostLoginDestination.AddCardiMember, route.Destination);
        // The wizard walks on into the device step by itself; nothing to resume over it.
        Assert.False(route.ResumeDeviceSetup);
    }

    [Fact]
    public void MemberWithoutDevice_LandsOnTheDashboardAndResumesDeviceSetup()
    {
        var route = PostLoginRouteResolver.Resolve(
            Status(account: true, member: true, device: false), deviceSetupDismissed: false);

        Assert.Equal(PostLoginDestination.Dashboard, route.Destination);
        Assert.True(route.ResumeDeviceSetup);
    }

    [Fact]
    public void MemberWithoutDevice_StopsResuming_OnceDismissed()
    {
        var route = PostLoginRouteResolver.Resolve(
            Status(account: true, member: true, device: false), deviceSetupDismissed: true);

        // Still the dashboard — dismissing the prompt must not cost the user the app.
        Assert.Equal(PostLoginDestination.Dashboard, route.Destination);
        Assert.False(route.ResumeDeviceSetup);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MemberWithDevice_JustGoesToTheDashboard(bool dismissed)
    {
        var route = PostLoginRouteResolver.Resolve(
            Status(account: true, member: true, device: true), deviceSetupDismissed: dismissed);

        Assert.Equal(PostLoginDestination.Dashboard, route.Destination);
        Assert.False(route.ResumeDeviceSetup);
    }

    [Fact]
    public void MissingStatus_GoesToAccountSetup()
    {
        var route = PostLoginRouteResolver.Resolve(null, deviceSetupDismissed: false);

        Assert.Equal(PostLoginDestination.AccountSetup, route.Destination);
        Assert.False(route.ResumeDeviceSetup);
    }

    [Fact]
    public void ResumeIsOnlyEverPairedWithTheDashboard()
    {
        // The launcher assumes a shell underneath the modal — no other destination has one.
        bool[] flags = [false, true];
        foreach (var account in flags)
            foreach (var member in flags)
                foreach (var device in flags)
                    foreach (var dismissed in flags)
                    {
                        var route = PostLoginRouteResolver.Resolve(Status(account, member, device), dismissed);
                        if (route.ResumeDeviceSetup)
                            Assert.Equal(PostLoginDestination.Dashboard, route.Destination);
                    }
    }
}
