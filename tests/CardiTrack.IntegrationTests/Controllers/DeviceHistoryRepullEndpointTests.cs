using CardiTrack.API.Controllers;
using CardiTrack.API.Infrastructure.UserContext;
using CardiTrack.API.Validators;
using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Exceptions;
using CardiTrack.Application.Interfaces.Services;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace CardiTrack.IntegrationTests.Controllers;

/// <summary>
/// The history re-pull endpoint (M1-15) queues work that spends a wearer's provider quota, so
/// each way it can be refused has to arrive as a distinct status — the mobile app decides what
/// to say from that alone — and a request that is taken must come back as a 202 with a body.
/// </summary>
public class DeviceHistoryRepullEndpointTests
{
    private readonly IDeviceHistoryRepullService _repulls = Substitute.For<IDeviceHistoryRepullService>();
    private readonly IUserContext _userContext = Substitute.For<IUserContext>();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _memberId = Guid.NewGuid();
    private readonly Guid _deviceId = Guid.NewGuid();

    private DevicesController CreateSut(bool authenticated = true)
    {
        _userContext.IsAuthenticated.Returns(authenticated);
        _userContext.UserId.Returns(authenticated ? _userId : Guid.Empty);

        // The real validator: the 400 path is the validator's, and stubbing it would leave the
        // test asserting a status the controller cannot produce on its own.
        return new DevicesController(
            _userContext,
            Substitute.For<ILogger<DevicesController>>(),
            Substitute.For<IDeviceConnectionService>(),
            Substitute.For<IManualDeviceSyncService>(),
            _repulls,
            Substitute.For<IValidator<ConnectDeviceRequest>>(),
            Substitute.For<IValidator<OAuthCallbackRequest>>(),
            new HistoryRepullValidator())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    private Task<ActionResult<ApiResponse<DeviceHistoryRepullResponse>>> Post(int days = 30, bool authenticated = true) =>
        CreateSut(authenticated).RequestHistoryRepull(
            _memberId, _deviceId, new HistoryRepullRequest { Days = days }, CancellationToken.None);

    private static int StatusOf(ActionResult<ApiResponse<DeviceHistoryRepullResponse>> result) =>
        result.Result switch
        {
            ObjectResult objectResult => objectResult.StatusCode ?? StatusCodes.Status200OK,
            _ => throw new InvalidOperationException($"Unexpected result {result.Result?.GetType().Name}"),
        };

    private void Refuse(string code) =>
        _repulls.RequestAsync(_userId, _memberId, _deviceId, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<Task<DeviceHistoryRepullResponse>>(_ => throw new HistoryRepullUnavailableException(code, "Nope."));

    [Fact]
    public async Task RequestHistoryRepull_WhenSignedOut_Is403_AndQueuesNothing()
    {
        var result = await Post(authenticated: false);

        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(result));
        await _repulls.DidNotReceiveWithAnyArgs().RequestAsync(default, default, default, default, default);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(91)]
    public async Task RequestHistoryRepull_WithDaysOutOfRange_Is400_AndQueuesNothing(int days)
    {
        var result = await Post(days);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(result));
        await _repulls.DidNotReceiveWithAnyArgs().RequestAsync(default, default, default, default, default);
    }

    // Denial is reported as 404 all the way up: a 403 would confirm the member exists.
    [Fact]
    public async Task RequestHistoryRepull_WhenMemberNotVisible_Is404()
    {
        _repulls.RequestAsync(_userId, _memberId, _deviceId, 30, Arg.Any<CancellationToken>())
            .Returns<Task<DeviceHistoryRepullResponse>>(_ => throw new KeyNotFoundException("CardiMember not found"));

        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(await Post()));
    }

    [Theory]
    [InlineData(HistoryRepullUnavailableException.MonitoringPaused)]
    [InlineData(HistoryRepullUnavailableException.DeviceNotSyncable)]
    [InlineData(HistoryRepullUnavailableException.RepullInProgress)]
    public async Task RequestHistoryRepull_WhenItCannotBeTaken_Is409(string code)
    {
        Refuse(code);

        Assert.Equal(StatusCodes.Status409Conflict, StatusOf(await Post()));
    }

    [Fact]
    public async Task RequestHistoryRepull_InsideTheCooldown_Is429()
    {
        Refuse(HistoryRepullUnavailableException.TooSoon);

        Assert.Equal(StatusCodes.Status429TooManyRequests, StatusOf(await Post()));
    }

    [Fact]
    public async Task RequestHistoryRepull_WhenQueued_Is202CarryingTheRequest()
    {
        _repulls.RequestAsync(_userId, _memberId, _deviceId, 30, Arg.Any<CancellationToken>())
            .Returns(new DeviceHistoryRepullResponse { Status = "pending", Days = 30 });

        var result = await Post();

        Assert.Equal(StatusCodes.Status202Accepted, StatusOf(result));
        var payload = Assert.IsType<ApiResponse<DeviceHistoryRepullResponse>>(
            Assert.IsType<ObjectResult>(result.Result).Value);
        Assert.True(payload.Success);
        Assert.Equal("pending", payload.Data!.Status);
        Assert.Equal(30, payload.Data.Days);
    }
}
