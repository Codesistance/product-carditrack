using CardiTrack.API.Controllers;
using CardiTrack.API.Infrastructure.UserContext;
using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Exceptions;
using CardiTrack.Application.Interfaces.Services;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace CardiTrack.IntegrationTests.Controllers;

/// <summary>
/// M1-15 Device Management (issue #1286): suspending, resuming, and the change/add outcomes of a
/// completed connection. The app decides what to tell the caregiver from the status alone, so a
/// clash with the member's current devices must arrive as 409 and nothing else.
/// </summary>
public class DeviceManagementEndpointTests
{
    private readonly IDeviceConnectionService _connections = Substitute.For<IDeviceConnectionService>();
    private readonly IValidator<OAuthCallbackRequest> _callbackValidator = Substitute.For<IValidator<OAuthCallbackRequest>>();
    private readonly IUserContext _userContext = Substitute.For<IUserContext>();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _memberId = Guid.NewGuid();
    private readonly Guid _deviceId = Guid.NewGuid();

    private DevicesController CreateSut(bool authenticated = true)
    {
        _userContext.IsAuthenticated.Returns(authenticated);
        _userContext.UserId.Returns(authenticated ? _userId : Guid.Empty);
        _callbackValidator.ValidateAsync(Arg.Any<OAuthCallbackRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ValidationResult());

        return new DevicesController(
            _userContext,
            Substitute.For<ILogger<DevicesController>>(),
            _connections,
            Substitute.For<IDeviceConnectionInviteService>(),
            Substitute.For<IManualDeviceSyncService>(),
            Substitute.For<IDeviceHistoryRepullService>(),
            Substitute.For<IValidator<ConnectDeviceRequest>>(),
            _callbackValidator,
            Substitute.For<IValidator<HistoryRepullRequest>>())
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

    [Fact]
    public async Task Suspend_ReturnsTheSuspendedDevice()
    {
        _connections.SuspendAsync(_userId, _memberId, _deviceId, Arg.Any<CancellationToken>())
            .Returns(new DeviceResponse { DeviceId = _deviceId, DisplayName = "Fitbit", Status = "suspended" });

        var result = await CreateSut().Suspend(_memberId, _deviceId, CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, StatusOf(result));
    }

    [Fact]
    public async Task Suspend_OfTheOnlyCollectingDevice_Is409()
    {
        _connections.SuspendAsync(_userId, _memberId, _deviceId, Arg.Any<CancellationToken>())
            .Returns<Task<DeviceResponse>>(_ => throw new DeviceConnectionException(
                DeviceConnectionException.LastActiveDevice, "Pause monitoring instead."));

        var result = await CreateSut().Suspend(_memberId, _deviceId, CancellationToken.None);

        Assert.Equal(StatusCodes.Status409Conflict, StatusOf(result));
    }

    // Denial is reported as 404 all the way up: a 403 would confirm the member exists.
    [Fact]
    public async Task Suspend_ByAViewOnlyCaregiver_Is404()
    {
        _connections.SuspendAsync(_userId, _memberId, _deviceId, Arg.Any<CancellationToken>())
            .Returns<Task<DeviceResponse>>(_ => throw new KeyNotFoundException("CardiMember not found"));

        var result = await CreateSut().Suspend(_memberId, _deviceId, CancellationToken.None);

        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(result));
    }

    [Fact]
    public async Task Suspend_WhenSignedOut_Is403_AndChangesNothing()
    {
        var result = await CreateSut(authenticated: false).Suspend(_memberId, _deviceId, CancellationToken.None);

        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(result));
        await _connections.DidNotReceiveWithAnyArgs().SuspendAsync(default, default, default, default);
    }

    [Fact]
    public async Task Resume_ReturnsTheDevice()
    {
        _connections.ResumeAsync(_userId, _memberId, _deviceId, Arg.Any<CancellationToken>())
            .Returns(new DeviceResponse { DeviceId = _deviceId, DisplayName = "Fitbit", Status = "active" });

        var result = await CreateSut().Resume(_memberId, _deviceId, CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, StatusOf(result));
    }

    [Fact]
    public async Task SetPrimary_OnASuspendedDevice_Is409()
    {
        _connections.SetPrimaryAsync(_userId, _memberId, _deviceId, Arg.Any<CancellationToken>())
            .Returns<Task<DeviceResponse>>(_ => throw new DeviceConnectionException(
                DeviceConnectionException.DeviceSuspended, "Resume it first."));

        var result = await CreateSut().SetPrimary(_memberId, _deviceId, CancellationToken.None);

        Assert.Equal(StatusCodes.Status409Conflict, StatusOf(result));
    }

    [Theory]
    [InlineData(DeviceConnectionException.DifferentAccount, StatusCodes.Status409Conflict)]
    [InlineData(DeviceConnectionException.AccountAlreadyConnected, StatusCodes.Status409Conflict)]
    [InlineData(DeviceConnectionException.OAuthExchangeFailed, StatusCodes.Status502BadGateway)]
    [InlineData(DeviceConnectionException.InvalidStateToken, StatusCodes.Status400BadRequest)]
    public async Task CompleteConnection_MapsEachRefusalToItsStatus(string code, int expected)
    {
        _connections.CompleteConnectionAsync(_userId, "fitbit", Arg.Any<OAuthCallbackRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<DeviceResponse>>(_ => throw new DeviceConnectionException(code, "refused"));

        var result = await CreateSut().CompleteConnection(
            "fitbit", new OAuthCallbackRequest { Code = "c", State = "s", CodeVerifier = "v" }, CancellationToken.None);

        Assert.Equal(expected, StatusOf(result));
    }

    [Fact]
    public async Task CompleteConnection_ForAReplacement_Is201_AndSaysSo()
    {
        var replaced = Guid.NewGuid();
        _connections.CompleteConnectionAsync(_userId, "fitbit", Arg.Any<OAuthCallbackRequest>(), Arg.Any<CancellationToken>())
            .Returns(new DeviceResponse { DeviceId = _deviceId, Status = "active", ReplacedDeviceId = replaced });

        var result = await CreateSut().CompleteConnection(
            "fitbit", new OAuthCallbackRequest { Code = "c", State = "s", CodeVerifier = "v" }, CancellationToken.None);

        Assert.Equal(StatusCodes.Status201Created, StatusOf(result));
        var body = Assert.IsType<ApiResponse<DeviceResponse>>(((ObjectResult)result.Result!).Value);
        Assert.Equal(replaced, body.Data!.ReplacedDeviceId);
        Assert.Contains("replaced", body.Message, StringComparison.OrdinalIgnoreCase);
    }
}
