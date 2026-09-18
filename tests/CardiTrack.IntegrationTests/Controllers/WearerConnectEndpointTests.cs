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
            Options.Create(new List<DeviceProviderSettings>
            {
                new()
                {
                    Provider = "GoogleHealth",
                    ClientId = "google-client",
                    AuthorizationUrl = "https://accounts.google.com/o/oauth2/v2/auth",
                },
                // A second block with the same host, and one with none, so the origin list is
                // exercised for de-duplication and for the config stubs that carry no URL yet.
                new()
                {
                    Provider = "GoogleHealthTwin",
                    ClientId = "google-client-twin",
                    AuthorizationUrl = "https://accounts.google.com/o/oauth2/v2/auth",
                },
                new() { Provider = "GarminConnect", ClientId = "", AuthorizationUrl = "" },
                // Configured URL but no client id: a roadmap stub nobody can connect through, and
                // so not something to widen the policy for.
                new()
                {
                    Provider = "Dropped",
                    ClientId = "",
                    AuthorizationUrl = "https://dropped.example.com/authorize",
                },
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
        Assert.Contains("Your heart rate", page.Content);
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
    public async Task Ask_PermitsTheProviderInFormAction_NotJustThisHost()
    {
        _invites.ViewAsync(Token, Arg.Any<CancellationToken>()).Returns(View);
        var sut = CreateSut();
        sut.Request.Scheme = "https";
        sut.Request.Host = new HostString("api.dev.example.com");

        await sut.Ask(Token, default);
        var csp = sut.Response.Headers.ContentSecurityPolicy.ToString();

        // Reported twice from a real device on 2026-09-18. "Continue" posts here and is redirected
        // straight to the provider, and browsers apply form-action across that redirect — so a
        // policy naming only this host refuses the submission, and refuses it silently with a
        // message that names our endpoint rather than the redirect. Both have to be listed.
        Assert.Contains("'self'", csp);
        Assert.Contains("https://api.dev.example.com", csp);
        Assert.Contains("https://accounts.google.com", csp);
    }

    [Fact]
    public async Task Ask_ListsEachProviderOriginOnce_AndSkipsUnconfiguredOnes()
    {
        _invites.ViewAsync(Token, Arg.Any<CancellationToken>()).Returns(View);
        var sut = CreateSut();

        await sut.Ask(Token, default);
        var csp = sut.Response.Headers.ContentSecurityPolicy.ToString();

        // Two blocks share the Google host and one has no URL at all: the directive should stay a
        // short, exact list rather than repeating itself or emitting an empty token.
        var formAction = csp.Split("form-action ")[1].Split(';')[0];
        Assert.Equal(1, formAction.Split("https://accounts.google.com").Length - 1);
        Assert.DoesNotContain("  ", formAction);
    }

    [Fact]
    public async Task Ask_OffersBothAnswers_AsFormsThatPostToUs()
    {
        _invites.ViewAsync(Token, Arg.Any<CancellationToken>()).Returns(View);
        var sut = CreateSut();

        var page = Page(await sut.Ask(Token, default));

        Assert.Contains("action=\"/connect/start\"", page.Content!);
        Assert.Contains("action=\"/connect/decline\"", page.Content);
        // Relative, so the form follows whichever host served the page rather than a baked-in one.
        Assert.DoesNotContain("action=\"http", page.Content);
    }

    [Fact]
    public async Task Ask_ExcludesProvidersNobodyCanConnectThrough()
    {
        _invites.ViewAsync(Token, Arg.Any<CancellationToken>()).Returns(View);
        var sut = CreateSut();

        await sut.Ask(Token, default);
        var csp = sut.Response.Headers.ContentSecurityPolicy.ToString();

        // A block with no client id cannot serve a grant, so naming its consent screen would widen
        // the policy for a provider nobody can reach — and the configuration still carries blocks
        // for unbuilt integrations and for two vendors dropped from the roadmap entirely.
        Assert.DoesNotContain("dropped.example.com", csp);
        Assert.Contains("https://accounts.google.com", csp);
    }

    [Fact]
    public async Task Ask_ShowsTheLogo_WithoutFetchingAnything()
    {
        _invites.ViewAsync(Token, Arg.Any<CancellationToken>()).Returns(View);
        var sut = CreateSut();

        var page = Page(await sut.Ask(Token, default));
        var csp = sut.Response.Headers.ContentSecurityPolicy.ToString();

        // Inlined, and the policy permits data URIs and nothing else — a page carrying a live
        // invitation token in its URL should not be opening image requests to anywhere, us
        // included.
        Assert.Contains("src=\"data:image/png;base64,", page.Content!);
        Assert.Contains("img-src data:", csp);
        Assert.DoesNotContain("img-src 'self'", csp);
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
    public async Task Start_GivesTheSameDeadEndAsAnUnknownToken_WhenSomethingBreaks()
    {
        _invites.StartAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("provider not configured"));
        var sut = CreateSut();
        var broken = Page(await sut.Start(Token, default));

        _invites.ViewAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((WearerInviteView?)null);
        var unknown = Page(await sut.Ask("a-token-that-names-nothing", default));

        // A provider with no configured bounce redirect throws here. Rendering anything distinctive
        // would tell a caller that this token was real and merely unusable, while an invented one
        // got the byte-identical 404 — exactly the difference the rest of this controller hides.
        Assert.Equal(unknown.StatusCode, broken.StatusCode);
        Assert.Equal(unknown.Content, broken.Content);
        Assert.DoesNotContain("provider not configured", broken.Content!);
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
