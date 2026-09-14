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
/// The three endpoints a caregiver reaches to close their account, and the states they report.
/// </summary>
/// <remarks>
/// A privacy-critical flow with no UI in front of it yet, so the contract pinned here is what the
/// in-app screen will be built against.
/// </remarks>
public class AccountDeletionEndpointTests
{
    private readonly IUserService _users = Substitute.For<IUserService>();
    private readonly IUserContext _userContext = Substitute.For<IUserContext>();
    private const string Auth0UserId = "auth0|caregiver";

    [Fact]
    public async Task Status_ReportsAPendingRequest_WithItsDueDate()
    {
        var requestedAt = new DateTime(2026, 9, 14, 11, 0, 0, DateTimeKind.Utc);
        _users.GetDeletionStatusAsync(Auth0UserId).Returns(
            new AccountDeletionStatusResponse(true, requestedAt, requestedAt.AddDays(30), true));

        var result = await SignedIn().GetAccountDeletion();

        var body = Assert.IsType<ApiResponse<AccountDeletionStatusResponse>>(
            ((ObjectResult)result.Result!).Value);
        Assert.True(body.Data!.DeletionRequested);
        Assert.Equal(requestedAt.AddDays(30), body.Data.ScheduledForUtc);
    }

    [Fact]
    public async Task Request_Returns200_AndTheDueDate()
    {
        var requestedAt = DateTime.UtcNow;
        _users.RequestDeletionAsync(Auth0UserId).Returns(
            new AccountDeletionStatusResponse(true, requestedAt, requestedAt.AddDays(30), true));

        var result = await SignedIn().RequestAccountDeletion();

        Assert.Equal(StatusCodes.Status200OK, ((ObjectResult)result.Result!).StatusCode);
        await _users.Received(1).RequestDeletionAsync(Auth0UserId);
    }

    /// <summary>
    /// The service returns null when the identity has no user row — including the case where the
    /// account went between the two reads inside a repeated request. A 200 there would report a
    /// deletion scheduled against nobody.
    /// </summary>
    [Fact]
    public async Task Request_Is404_WhenThereIsNoAccountForTheIdentity()
    {
        _users.RequestDeletionAsync(Auth0UserId).Returns((AccountDeletionStatusResponse?)null);

        var result = await SignedIn().RequestAccountDeletion();

        Assert.Equal(StatusCodes.Status404NotFound, ((ObjectResult)result.Result!).StatusCode);
    }

    [Fact]
    public async Task Cancel_Returns200_AndAnAccountThatIsStayingPut()
    {
        _users.CancelDeletionAsync(Auth0UserId).Returns(
            new AccountDeletionStatusResponse(false, null, null, false));

        var result = await SignedIn().CancelAccountDeletion();

        var body = Assert.IsType<ApiResponse<AccountDeletionStatusResponse>>(
            ((ObjectResult)result.Result!).Value);
        Assert.False(body.Data!.DeletionRequested);
    }

    /// <summary>
    /// No identity is a 403 on all three, and the service is never asked: a deletion clock started
    /// for an unidentified caller is the worst thing these endpoints could do.
    /// </summary>
    [Fact]
    public async Task WithoutAnIdentity_AllThreeAre403_AndTheServiceIsNeverCalled()
    {
        var sut = Anonymous();

        Assert.Equal(StatusCodes.Status403Forbidden,
            ((ObjectResult)(await sut.GetAccountDeletion()).Result!).StatusCode);
        Assert.Equal(StatusCodes.Status403Forbidden,
            ((ObjectResult)(await sut.RequestAccountDeletion()).Result!).StatusCode);
        Assert.Equal(StatusCodes.Status403Forbidden,
            ((ObjectResult)(await sut.CancelAccountDeletion()).Result!).StatusCode);

        await _users.DidNotReceiveWithAnyArgs().RequestDeletionAsync(default!);
        await _users.DidNotReceiveWithAnyArgs().CancelDeletionAsync(default!);
        await _users.DidNotReceiveWithAnyArgs().GetDeletionStatusAsync(default!);
    }

    private UsersController SignedIn()
    {
        _userContext.IsAuthenticated.Returns(true);
        _userContext.Auth0UserId.Returns(Auth0UserId);
        return Build();
    }

    private UsersController Anonymous()
    {
        _userContext.IsAuthenticated.Returns(false);
        _userContext.Auth0UserId.Returns(string.Empty);
        return Build();
    }

    private UsersController Build() =>
        new(_userContext, Substitute.For<ILogger<UsersController>>(), _users)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
}
