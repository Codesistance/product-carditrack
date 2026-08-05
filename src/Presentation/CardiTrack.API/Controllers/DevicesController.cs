using CardiTrack.API.Infrastructure.UserContext;
using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Exceptions;
using CardiTrack.Application.Interfaces.Services;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CardiTrack.API.Controllers;

[Authorize]
[Route("api/v1")]
public class DevicesController : BaseApiController
{
    private readonly IDeviceConnectionService _deviceConnections;
    private readonly IValidator<ConnectDeviceRequest> _connectValidator;
    private readonly IValidator<OAuthCallbackRequest> _callbackValidator;

    public DevicesController(
        IUserContext userContext,
        ILogger<DevicesController> logger,
        IDeviceConnectionService deviceConnections,
        IValidator<ConnectDeviceRequest> connectValidator,
        IValidator<OAuthCallbackRequest> callbackValidator)
        : base(userContext, logger)
    {
        _deviceConnections = deviceConnections;
        _connectValidator = connectValidator;
        _callbackValidator = callbackValidator;
    }

    /// <summary>All wearable connections for one CardiMember (M1-05 / M1-15).</summary>
    [HttpGet("cardimembers/{cardiMemberId:guid}/devices")]
    [ProducesResponseType(typeof(ApiResponse<DeviceListResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<DeviceListResponse>>> GetDevices(
        Guid cardiMemberId, CancellationToken ct)
    {
        if (!UserContext.IsAuthenticated || UserContext.UserId == Guid.Empty)
        {
            return Error("We couldn't find your account — please sign in again.", StatusCodes.Status403Forbidden);
        }

        try
        {
            var result = await _deviceConnections.GetDevicesAsync(UserContext.UserId, cardiMemberId, ct);
            return Success(result);
        }
        catch (KeyNotFoundException ex)
        {
            return Error(ex.Message, StatusCodes.Status404NotFound);
        }
    }

    /// <summary>Initiates a PKCE server-OAuth connection; returns the provider authorization URL (M1-06).</summary>
    [HttpPost("cardimembers/{cardiMemberId:guid}/devices")]
    [ProducesResponseType(typeof(ApiResponse<OAuthInitiationResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<OAuthInitiationResponse>>> InitiateConnection(
        Guid cardiMemberId, [FromBody] ConnectDeviceRequest request, CancellationToken ct)
    {
        if (!UserContext.IsAuthenticated || UserContext.UserId == Guid.Empty)
        {
            return Error("We couldn't find your account — please sign in again.", StatusCodes.Status403Forbidden);
        }

        var validation = await _connectValidator.ValidateAsync(request, ct);
        if (!validation.IsValid)
        {
            return ValidationFailed(validation);
        }

        try
        {
            var result = await _deviceConnections.InitiateConnectionAsync(
                UserContext.UserId, cardiMemberId, request, ct);
            return Success(result);
        }
        catch (KeyNotFoundException ex)
        {
            return Error(ex.Message, StatusCodes.Status404NotFound);
        }
        catch (DeviceConnectionException ex)
        {
            return Error(ex.Message, StatusCodes.Status400BadRequest);
        }
    }

    /// <summary>
    /// Provider-facing https redirect target (Google web clients cannot redirect to a custom
    /// scheme). Bounces the browser back into the mobile app's deep link with code + state; the
    /// app then completes the flow via the authenticated callback endpoint below.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("oauth/redirect/{provider}")]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> RedirectToApp(
        string provider, [FromQuery] string? code, [FromQuery] string? state, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
        {
            return Error("That connection link looks incomplete — please start the device connection again.",
                StatusCodes.Status400BadRequest);
        }

        var appRedirectUri = await _deviceConnections.GetAppRedirectUriAsync(provider, state, ct);
        if (appRedirectUri is null)
        {
            return Error("That connection link has expired — please start the device connection again.",
                StatusCodes.Status400BadRequest);
        }

        var separator = appRedirectUri.Contains('?') ? '&' : '?';
        return Redirect(
            $"{appRedirectUri}{separator}code={Uri.EscapeDataString(code)}&state={Uri.EscapeDataString(state)}");
    }

    /// <summary>Completes the OAuth flow: exchanges the code + PKCE verifier and stores the connection (M1-07).</summary>
    [HttpPost("oauth/callback/{provider}")]
    [ProducesResponseType(typeof(ApiResponse<DeviceResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<ApiResponse<DeviceResponse>>> CompleteConnection(
        string provider, [FromBody] OAuthCallbackRequest request, CancellationToken ct)
    {
        if (!UserContext.IsAuthenticated || UserContext.UserId == Guid.Empty)
        {
            return Error("We couldn't find your account — please sign in again.", StatusCodes.Status403Forbidden);
        }

        var validation = await _callbackValidator.ValidateAsync(request, ct);
        if (!validation.IsValid)
        {
            return ValidationFailed(validation);
        }

        try
        {
            var result = await _deviceConnections.CompleteConnectionAsync(
                UserContext.UserId, provider, request, ct);
            return Created(result, "Your device is connected and ready to go!");
        }
        catch (KeyNotFoundException ex)
        {
            return Error(ex.Message, StatusCodes.Status404NotFound);
        }
        catch (DeviceConnectionException ex)
        {
            Logger.LogWarning(ex, "Device OAuth completion failed with code {Code}", ex.Code);
            var status = ex.Code == DeviceConnectionException.OAuthExchangeFailed
                ? StatusCodes.Status502BadGateway
                : StatusCodes.Status400BadRequest;
            return Error(ex.Message, status);
        }
    }
}
