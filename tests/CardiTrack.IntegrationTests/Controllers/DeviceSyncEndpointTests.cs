using CardiTrack.API.Controllers;
using CardiTrack.API.Infrastructure.UserContext;
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
/// The manual sync endpoint (issue #67) is the only route by which a caregiver's tap reaches a
/// wearable provider, so each way it can be refused has to arrive as a distinct status — the
/// mobile app decides what to say from that alone.
/// </summary>
public class DeviceSyncEndpointTests
{
    private readonly IManualDeviceSyncService _manualSync = Substitute.For<IManualDeviceSyncService>();
    private readonly IUserContext _userContext = Substitute.For<IUserContext>();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _memberId = Guid.NewGuid();

    private DevicesController CreateSut(bool authenticated = true)
    {
        _userContext.IsAuthenticated.Returns(authenticated);
        _userContext.UserId.Returns(authenticated ? _userId : Guid.Empty);

        return new DevicesController(
            _userContext,
            Substitute.For<ILogger<DevicesController>>(),
            Substitute.For<IDeviceConnectionService>(),
            _manualSync,
            Substitute.For<IValidator<ConnectDeviceRequest>>(),
            Substitute.For<IValidator<OAuthCallbackRequest>>())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    private static int StatusOf(ActionResult<ApiResponse<DeviceSyncResultResponse>> result) =>
        result.Result switch
        {
            ObjectResult objectResult => objectResult.StatusCode ?? StatusCodes.Status200OK,
            _ => throw new InvalidOperationException($"Unexpected result {result.Result?.GetType().Name}"),
        };

    [Fact]
    public async Task SyncDevices_WhenSignedOut_Is403_AndNeverSyncs()
    {
        var result = await CreateSut(authenticated: false).SyncDevices(_memberId, CancellationToken.None);

        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(result));
        await _manualSync.DidNotReceiveWithAnyArgs()
            .SyncMemberAsync(default, default, default);
    }

    // Denial is reported as 404 all the way up: a 403 would confirm the member exists.
    [Fact]
    public async Task SyncDevices_WhenMemberNotVisible_Is404()
    {
        _manualSync.SyncMemberAsync(_userId, _memberId, Arg.Any<CancellationToken>())
            .Returns<Task<DeviceSyncResultResponse>>(_ => throw new KeyNotFoundException("CardiMember not found"));

        var result = await CreateSut().SyncDevices(_memberId, CancellationToken.None);

        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(result));
    }

    [Fact]
    public async Task SyncDevices_WhenRateLimited_Is429()
    {
        _manualSync.SyncMemberAsync(_userId, _memberId, Arg.Any<CancellationToken>())
            .Returns<Task<DeviceSyncResultResponse>>(_ => throw new ManualSyncUnavailableException(
                ManualSyncUnavailableException.TooSoon, "We checked in moments ago."));

        var result = await CreateSut().SyncDevices(_memberId, CancellationToken.None);

        Assert.Equal(StatusCodes.Status429TooManyRequests, StatusOf(result));
    }

    [Theory]
    [InlineData(ManualSyncUnavailableException.MonitoringPaused)]
    [InlineData(ManualSyncUnavailableException.NoConnectedDevice)]
    public async Task SyncDevices_WhenSyncCannotRun_Is409(string code)
    {
        _manualSync.SyncMemberAsync(_userId, _memberId, Arg.Any<CancellationToken>())
            .Returns<Task<DeviceSyncResultResponse>>(_ => throw new ManualSyncUnavailableException(code, "Nope."));

        var result = await CreateSut().SyncDevices(_memberId, CancellationToken.None);

        Assert.Equal(StatusCodes.Status409Conflict, StatusOf(result));
    }

    // A provider that failed still leaves the request successful — the body carries which one.
    [Fact]
    public async Task SyncDevices_WithAPartialFailure_Is200CarryingTheDetail()
    {
        _manualSync.SyncMemberAsync(_userId, _memberId, Arg.Any<CancellationToken>())
            .Returns(new DeviceSyncResultResponse
            {
                SyncedCount = 1,
                FailedCount = 1,
                Devices =
                [
                    new DeviceSyncOutcome { Provider = "fitbit", Succeeded = true },
                    new DeviceSyncOutcome { Provider = "garmin", Message = "We couldn't reach this device's provider." },
                ],
            });

        var result = await CreateSut().SyncDevices(_memberId, CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, StatusOf(result));
        var payload = Assert.IsType<ApiResponse<DeviceSyncResultResponse>>(
            Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(1, payload.Data!.FailedCount);
    }
}
