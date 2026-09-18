using CardiTrack.API.Controllers;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Infrastructure.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace CardiTrack.IntegrationTests.Controllers;

/// <summary>
/// The anonymous pages a wearer reaches by following a caregiver's invitation.
/// </summary>
/// <remarks>
/// These are the only pages in the product served to somebody with no account, over a credential
/// that may have been forwarded, mis-sent or left on a screen. So most of what is asserted here is
/// about restraint: what the page will say, what it refuses to say differently for a real token than
/// an invented one, and what it will not do on a GET.
/// </remarks>
public class WearerConnectEndpointTests
{
    private const string Token = "a-live-invite-token";

    private readonly IDeviceConnectionInviteService _invites =
        Substitute.For<IDeviceConnectionInviteService>();

    private static readonly WearerInviteView View = new(
        MemberFirstName: "Margaret",
        CaregiverFirstName: "John",
        DeviceDisplayName: "Fitbit",
        Provider: "fitbit",
        ExpiresAt: new DateTime(2026, 9, 19, 9, 0, 0, DateTimeKind.Utc));

    private WearerConnectController CreateSut() =>
        new(
            _invites,
            Options.Create(new DeviceInviteOptions
            {
                PrivacyPolicyUrl = "https://carditrack.example/privacy",
            }),
            NullLogger<WearerConnectController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

    private static ContentResult Page(IActionResult result)
    {
        var content = Assert.IsType<ContentResult>(result);
        Assert.Equal("text/html", content.ContentType);
        Assert.NotNull(content.Content);
        return content;
    }

    [Fact]
    public async Task Ask_NamesWhoIsAsking_AndWhatWouldBeShared()
    {
        _invites.ViewAsync(Token, Arg.Any<CancellationToken>()).Returns(View);
        var sut = CreateSut();

        var page = Page(await sut.Ask(Token, default));

        Assert.Equal(StatusCodes.Status200OK, page.StatusCode);
        // Informed consent needs all three: who, about whom, and what.
        Assert.Contains("John", page.Content!);
        Assert.Contains("Margaret", page.Content);
        Assert.Contains("Fitbit", page.Content);
        Assert.Contains("Heart rate", page.Content);
        Assert.Contains("https://carditrack.example/privacy", page.Content);
    }

    [Fact]
    public async Task Ask_LinksGooglesPermissionsScreen_ForAGoogleBackedBrand()
    {
        _invites.ViewAsync(Token, Arg.Any<CancellationToken>()).Returns(View);
        var sut = CreateSut();

        var page = Page(await sut.Ask(Token, default));

        Assert.Contains("https://myaccount.google.com/permissions", page.Content!);
    }

    [Fact]
    public async Task Ask_NamesNoProvidersScreen_ForABrandThatDoesNotUseOne()
    {
        _invites.ViewAsync(Token, Arg.Any<CancellationToken>())
            .Returns(View with { Provider = "garmin", DeviceDisplayName = "Garmin" });
        var sut = CreateSut();

        var page = Page(await sut.Ask(Token, default));

        // Sending a Garmin wearer to Google's permissions screen would show them a page that has
        // never heard of them, and the one instruction about taking access back would be wrong.
        Assert.DoesNotContain("myaccount.google.com", page.Content!);
        Assert.Contains("Garmin account settings", page.Content);
    }

    [Fact]
    public async Task Ask_SendsHeadersThatKeepTheTokenFromTravelling()
    {
        _invites.ViewAsync(Token, Arg.Any<CancellationToken>()).Returns(View);
        var sut = CreateSut();

        await sut.Ask(Token, default);
        var headers = sut.Response.Headers;

        // The URL carries a live invitation. Out of caches, out of the Referer of anything this
        // page links to, and out of a frame on somebody else's site.
        Assert.Equal("no-store", headers.CacheControl);
        Assert.Equal("no-referrer", headers["Referrer-Policy"]);
        Assert.Equal("DENY", headers.XFrameOptions);
        Assert.Contains("form-action 'self'", headers.ContentSecurityPolicy.ToString());
        // No script at all, so a content injection would have nothing to execute.
        Assert.Contains("default-src 'none'", headers.ContentSecurityPolicy.ToString());
    }

    [Fact]
    public async Task Ask_CarriesNoScript()
    {
        _invites.ViewAsync(Token, Arg.Any<CancellationToken>()).Returns(View);
        var sut = CreateSut();

        var page = Page(await sut.Ask(Token, default));

        // The content security policy forbids script; the page must not need any either, or it
        // would simply be broken rather than safe.
        Assert.DoesNotContain("<script", page.Content!);
    }

    [Fact]
    public async Task Ask_GivesByteForByteTheSameAnswer_ForEveryLinkThatCannotBeUsed()
    {
        _invites.ViewAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((WearerInviteView?)null);
        var sut = CreateSut();

        var pages = new List<ContentResult>();
        foreach (var token in new[] { null, "", "   ", "a-token-that-names-nothing" })
            pages.Add(Page(await sut.Ask(token, default)));

        // Unknown, expired, spent, declined and revoked all land here. The copy hedges — "used, or
        // expired" — precisely so it can be identical for all of them, and the assertion is that
        // identity rather than the wording: a page that varied at all would answer, for any token
        // somebody cared to try, whether it had ever been a real invitation. The status must not
        // vary either; 404 against 410 would say it just as loudly as the words would.
        Assert.All(pages, p => Assert.Equal(StatusCodes.Status404NotFound, p.StatusCode));
        Assert.All(pages, p => Assert.Equal(pages[0].Content, p.Content));

        // And nothing about the invitation the token might have named.
        Assert.DoesNotContain("Margaret", pages[0].Content!);
        Assert.DoesNotContain("John", pages[0].Content);
    }

    [Fact]
    public async Task Start_RedirectsToTheProvider()
    {
        _invites.StartAsync(Token, Arg.Any<CancellationToken>())
            .Returns("https://accounts.google.com/o/oauth2/v2/auth?state=abc");
        var sut = CreateSut();

        var redirect = Assert.IsType<RedirectResult>(await sut.Start(Token, default));

        Assert.StartsWith("https://accounts.google.com/", redirect.Url);
    }

    [Fact]
    public async Task Start_RendersTheDeadEnd_WhenTheInviteIsNoLongerLive()
    {
        _invites.StartAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((string?)null);
        var sut = CreateSut();

        var page = Page(await sut.Start(Token, default));

        Assert.Equal(StatusCodes.Status404NotFound, page.StatusCode);
    }

    [Fact]
    public async Task Start_SaysNothingWasShared_WhenSomethingBreaks()
    {
        _invites.StartAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("provider not configured"));
        var sut = CreateSut();

        var page = Page(await sut.Start(Token, default));

        // The wearer is told the truth — nothing was shared — and offered another go. The detail
        // belongs in the log, not on a page served to whoever holds the link.
        Assert.Equal(StatusCodes.Status200OK, page.StatusCode);
        Assert.Contains("Nothing was shared", page.Content!);
        Assert.DoesNotContain("provider not configured", page.Content);
        Assert.Contains("Try again", page.Content);
    }

    [Fact]
    public async Task Decline_AnswersTheSameWay_WhetherOrNotThereWasAnythingToDecline()
    {
        _invites.DeclineAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);
        var sut = CreateSut();

        var page = Page(await sut.Decline("a-token-that-names-nothing", default));

        // Somebody sent this by mistake should be told the matter is closed; somebody probing
        // tokens should learn nothing from the difference.
        Assert.Equal(StatusCodes.Status200OK, page.StatusCode);
        Assert.Contains("nothing was shared", page.Content!);
    }

    [Fact]
    public void StartAndDecline_AreNotReachableByGet()
    {
        // A GET would let any preview fetch — a messaging app unfurling the link, a mail scanner —
        // start or end the flow before the wearer had read a word of it.
        foreach (var name in new[] { nameof(WearerConnectController.Start), nameof(WearerConnectController.Decline) })
        {
            var method = typeof(WearerConnectController).GetMethod(name)!;
            Assert.Single(method.GetCustomAttributes(typeof(HttpPostAttribute), inherit: false));
            Assert.Empty(method.GetCustomAttributes(typeof(HttpGetAttribute), inherit: false));
        }
    }
}
