using System.Security.Claims;
using Bunit;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Web.Components.Shared;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace CardiTrack.UnitTests.Web;

public class HealthDataDisclosureBannerTests : BunitContext
{
    private const string Auth0UserId = "auth0|abc";
    private const string DisclosureText =
        "CardiTrack collects health and fitness data to enable anomaly alerts, daily health digests, and trend monitoring.";

    private readonly IUserService _userService = Substitute.For<IUserService>();

    public HealthDataDisclosureBannerTests()
    {
        Services.AddSingleton(_userService);
    }

    private void AuthorizeUser()
    {
        AddAuthorization()
            .SetAuthorized("Jane Carer")
            .SetClaims(new Claim(ClaimTypes.NameIdentifier, Auth0UserId));
    }

    [Fact]
    public void Banner_IsShown_WithDisclosureTextAndPrivacyLink_WhenNotDismissed()
    {
        AuthorizeUser();
        _userService.HasDismissedHealthDataDisclosureAsync(Auth0UserId).Returns(false);

        var cut = Render<HealthDataDisclosureBanner>();

        Assert.Contains(DisclosureText, cut.Find(".health-disclosure-banner").TextContent);
        Assert.Equal("/privacy", cut.Find(".health-disclosure-banner a").GetAttribute("href"));
        Assert.NotNull(cut.Find("button[aria-label=Dismiss]"));
    }

    [Fact]
    public void Banner_IsNotRendered_WhenAlreadyDismissed()
    {
        AuthorizeUser();
        _userService.HasDismissedHealthDataDisclosureAsync(Auth0UserId).Returns(true);

        var cut = Render<HealthDataDisclosureBanner>();

        Assert.Empty(cut.Markup.Trim());
    }

    [Fact]
    public void Banner_IsNotRendered_AndServiceNotQueried_WhenUnauthenticated()
    {
        AddAuthorization().SetNotAuthorized();

        var cut = Render<HealthDataDisclosureBanner>();

        Assert.Empty(cut.Markup.Trim());
        _userService.DidNotReceiveWithAnyArgs().HasDismissedHealthDataDisclosureAsync(default!);
    }

    [Fact]
    public void DismissClick_PersistsDismissalAndHidesBanner()
    {
        AuthorizeUser();
        _userService.HasDismissedHealthDataDisclosureAsync(Auth0UserId).Returns(false);
        _userService.DismissHealthDataDisclosureAsync(Auth0UserId).Returns(true);

        var cut = Render<HealthDataDisclosureBanner>();
        cut.Find("button[aria-label=Dismiss]").Click();

        Assert.Empty(cut.Markup.Trim());
        _userService.Received(1).DismissHealthDataDisclosureAsync(Auth0UserId);
    }
}
