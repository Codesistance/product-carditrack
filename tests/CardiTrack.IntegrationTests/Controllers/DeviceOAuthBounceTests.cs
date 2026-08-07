using CardiTrack.API.Controllers;
using CardiTrack.API.Infrastructure.UserContext;
using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.Interfaces.Services;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace CardiTrack.IntegrationTests.Controllers;

/// <summary>
/// The anonymous bounce endpoint is the only thing that dismisses the in-app browser, so
/// every provider outcome has to leave here pointed at the app deep link. A response that
/// ends in the browser strands the user on the consent page with the app still waiting.
/// </summary>
public class DeviceOAuthBounceTests
{
    private const string DeepLink = "carditrack://oauth/callback";

    private readonly IDeviceConnectionService _connections = Substitute.For<IDeviceConnectionService>();

    private DevicesController CreateSut(string? appRedirectUri = DeepLink)
    {
        _connections.GetAppRedirectUriAsync("fitbit", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(appRedirectUri);

        return new DevicesController(
            Substitute.For<IUserContext>(),
            Substitute.For<ILogger<DevicesController>>(),
            _connections,
            Substitute.For<IValidator<ConnectDeviceRequest>>(),
            Substitute.For<IValidator<OAuthCallbackRequest>>())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    private static ContentResult Bounce(IActionResult result)
    {
        var content = Assert.IsType<ContentResult>(result);
        Assert.Equal("text/html", content.ContentType);
        Assert.NotNull(content.Content);
        return content;
    }

    [Fact]
    public async Task HandsOffToTheApp_WithCodeAndState()
    {
        var sut = CreateSut();

        var page = Bounce(await sut.RedirectToApp("fitbit", "auth_code", "state_1", null, null, default));

        Assert.Equal(StatusCodes.Status200OK, page.StatusCode);
        Assert.Contains(DeepLink, page.Content!);
        Assert.Contains("state=state_1", page.Content);
        Assert.Contains("code=auth_code", page.Content);
        // A Location header naming a custom scheme is dropped by browsers that only forward
        // http(s), so the navigation has to happen from the page itself.
        Assert.Contains("location.replace", page.Content);
        Assert.Equal("no-store", sut.Response.Headers.CacheControl);
        Assert.Equal("no-referrer", sut.Response.Headers["Referrer-Policy"]);
    }

    [Fact]
    public async Task HandsOffToTheApp_WhenTheWearerDeniesConsent()
    {
        var sut = CreateSut();

        var page = Bounce(await sut.RedirectToApp("fitbit", null, "state_1", "access_denied", "denied by user", default));

        Assert.Equal(StatusCodes.Status200OK, page.StatusCode);
        Assert.Contains(DeepLink, page.Content!);
        Assert.Contains("state=state_1", page.Content);
        Assert.Contains("error=access_denied", page.Content);
        Assert.DoesNotContain("code=", page.Content);
    }

    [Fact]
    public async Task ForwardsAGenericError_WhenTheProviderNamesNone()
    {
        var sut = CreateSut();

        var page = Bounce(await sut.RedirectToApp("fitbit", null, "state_1", null, null, default));

        Assert.Contains("error=invalid_request", page.Content!);
    }

    [Fact]
    public async Task EncodesProviderSuppliedText_IntoTheHandoffPage()
    {
        var sut = CreateSut();

        var page = Bounce(await sut.RedirectToApp(
            "fitbit", null, "state_1", "access_denied", "\"><script>alert(1)</script>", default));

        Assert.DoesNotContain("alert(1)", page.Content!);
        Assert.Contains("%3Cscript%3E", page.Content);
    }

    [Theory]
    [InlineData(null)]      // no state at all
    [InlineData("state_1")] // a state the cache no longer knows
    public async Task RendersATerminalPage_WhenThereIsNoDeepLinkToReach(string? state)
    {
        var sut = CreateSut(appRedirectUri: null);

        var page = Bounce(await sut.RedirectToApp("fitbit", "auth_code", state, null, null, default));

        Assert.Equal(StatusCodes.Status400BadRequest, page.StatusCode);
        // Nothing to hand off to, and nothing that could leak the code onwards.
        Assert.DoesNotContain("carditrack://", page.Content!);
        Assert.DoesNotContain("auth_code", page.Content);
    }
}
