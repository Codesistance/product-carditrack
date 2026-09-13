using CardiTrack.API.Controllers;
using CardiTrack.API.Infrastructure.UserContext;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Interfaces.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace CardiTrack.IntegrationTests.Controllers;

/// <summary>
/// The disclosure endpoints are what the mobile banner decides from, so the answers that would
/// hide a compliance notice wrongly — or record an acknowledgement against nobody — are the ones
/// that matter.
/// </summary>
public class UsersHealthDataDisclosureEndpointTests
{
    private const string Auth0Id = "auth0|caregiver";

    private readonly IUserService _users = Substitute.For<IUserService>();
    private readonly IUserContext _userContext = Substitute.For<IUserContext>();

    private UsersController CreateSut(bool authenticated = true)
    {
        _userContext.IsAuthenticated.Returns(authenticated);
        _userContext.Auth0UserId.Returns(authenticated ? Auth0Id : string.Empty);
        _userContext.UserId.Returns(authenticated ? Guid.NewGuid() : Guid.Empty);

        return new UsersController(_userContext, Substitute.For<ILogger<UsersController>>(), _users)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    private static int StatusOf<T>(ActionResult<ApiResponse<T>> result) =>
        result.Result switch
        {
            ObjectResult objectResult => objectResult.StatusCode ?? StatusCodes.Status200OK,
            _ => throw new InvalidOperationException($"Unexpected result {result.Result?.GetType().Name}"),
        };

    private static T DataOf<T>(ActionResult<ApiResponse<T>> result) =>
        ((ApiResponse<T>)((ObjectResult)result.Result!).Value!).Data!;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Get_ReportsWhetherTheCallerHasDismissed(bool dismissed)
    {
        _users.HasDismissedHealthDataDisclosureAsync(Auth0Id).Returns(dismissed);

        var result = await CreateSut().GetHealthDataDisclosure();

        Assert.Equal(StatusCodes.Status200OK, StatusOf(result));
        Assert.Equal(dismissed, DataOf(result).Dismissed);
    }

    [Fact]
    public async Task Get_RefusesAnUnauthenticatedCaller()
    {
        var result = await CreateSut(authenticated: false).GetHealthDataDisclosure();

        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(result));
        await _users.DidNotReceiveWithAnyArgs().HasDismissedHealthDataDisclosureAsync(default!);
    }

    [Fact]
    public async Task Dismiss_RecordsTheAcknowledgement()
    {
        _users.DismissHealthDataDisclosureAsync(Auth0Id).Returns(true);

        var result = await CreateSut().DismissHealthDataDisclosure();

        Assert.Equal(StatusCodes.Status200OK, StatusOf(result));
        await _users.Received(1).DismissHealthDataDisclosureAsync(Auth0Id);
    }

    [Fact]
    public async Task Dismiss_Is404_WhenThereIsNoUserRowToRecordItOn()
    {
        // A false from the service means no account yet — the banner must keep showing rather
        // than be treated as acknowledged.
        _users.DismissHealthDataDisclosureAsync(Auth0Id).Returns(false);

        var result = await CreateSut().DismissHealthDataDisclosure();

        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(result));
    }

    [Fact]
    public async Task Dismiss_RefusesAnUnauthenticatedCaller()
    {
        var result = await CreateSut(authenticated: false).DismissHealthDataDisclosure();

        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(result));
        await _users.DidNotReceiveWithAnyArgs().DismissHealthDataDisclosureAsync(default!);
    }
}
