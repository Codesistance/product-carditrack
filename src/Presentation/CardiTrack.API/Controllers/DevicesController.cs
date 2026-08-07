using System.Net.Mime;
using System.Text;
using CardiTrack.API.Infrastructure.OAuth;
using CardiTrack.API.Infrastructure.Auditing;
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
[AuditHealthDataAccess("AccessDevice", EntityType = "DeviceConnection")]
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

    /// <summary>Disconnects and forgets a device (M1-15 "Remove Device").</summary>
    [HttpDelete("cardimembers/{cardiMemberId:guid}/devices/{deviceId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult> Disconnect(Guid cardiMemberId, Guid deviceId, CancellationToken ct)
    {
        if (!UserContext.IsAuthenticated || UserContext.UserId == Guid.Empty)
        {
            return Error("We couldn't find your account — please sign in again.", StatusCodes.Status403Forbidden);
        }

        try
        {
            await _deviceConnections.DisconnectAsync(UserContext.UserId, cardiMemberId, deviceId, ct);
            Logger.LogInformation(
                "Device {DeviceId} disconnected from CardiMember {CardiMemberId} by user {UserId}",
                deviceId, cardiMemberId, UserContext.UserId);
            return NoContent();
        }
        catch (KeyNotFoundException ex)
        {
            return Error(ex.Message, StatusCodes.Status404NotFound);
        }
    }

    /// <summary>Makes a device the CardiMember's primary data source (M1-15).</summary>
    [HttpPost("cardimembers/{cardiMemberId:guid}/devices/{deviceId:guid}/primary")]
    [ProducesResponseType(typeof(ApiResponse<DeviceResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<DeviceResponse>>> SetPrimary(
        Guid cardiMemberId, Guid deviceId, CancellationToken ct)
    {
        if (!UserContext.IsAuthenticated || UserContext.UserId == Guid.Empty)
        {
            return Error("We couldn't find your account — please sign in again.", StatusCodes.Status403Forbidden);
        }

        try
        {
            var device = await _deviceConnections.SetPrimaryAsync(UserContext.UserId, cardiMemberId, deviceId, ct);
            return Success(device, $"{device.DisplayName} is now the primary device.");
        }
        catch (KeyNotFoundException ex)
        {
            return Error(ex.Message, StatusCodes.Status404NotFound);
        }
    }

    /// <summary>Renews an expired OAuth token and reports connection state (M1-15 "Refresh Connection").</summary>
    [HttpPost("cardimembers/{cardiMemberId:guid}/devices/{deviceId:guid}/refresh")]
    [ProducesResponseType(typeof(ApiResponse<DeviceResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<ApiResponse<DeviceResponse>>> RefreshConnection(
        Guid cardiMemberId, Guid deviceId, CancellationToken ct)
    {
        if (!UserContext.IsAuthenticated || UserContext.UserId == Guid.Empty)
        {
            return Error("We couldn't find your account — please sign in again.", StatusCodes.Status403Forbidden);
        }

        try
        {
            var device = await _deviceConnections.RefreshConnectionAsync(
                UserContext.UserId, cardiMemberId, deviceId, ct);
            return Success(device, "Connection refreshed.");
        }
        catch (KeyNotFoundException ex)
        {
            return Error(ex.Message, StatusCodes.Status404NotFound);
        }
        catch (DeviceConnectionException ex)
        {
            Logger.LogWarning(ex, "Device refresh failed with code {Code}", ex.Code);
            var status = ex.Code == DeviceConnectionException.OAuthExchangeFailed
                ? StatusCodes.Status502BadGateway
                : StatusCodes.Status400BadRequest;
            return Error(ex.Message, status);
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
    /// scheme). Hands the browser back into the mobile app's deep link; the app then completes
    /// the flow via the authenticated callback endpoint below.
    ///
    /// Every outcome the provider can produce — including a denied consent — has to reach the
    /// app, because the deep link is the only thing that dismisses the in-app browser. Ending
    /// the response here instead would leave the user on the consent page with the app still
    /// waiting behind it.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("oauth/redirect/{provider}")]
    [Produces(MediaTypeNames.Text.Html)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> RedirectToApp(
        string provider,
        [FromQuery] string? code,
        [FromQuery] string? state,
        [FromQuery] string? error,
        [FromQuery(Name = "error_description")] string? errorDescription,
        CancellationToken ct)
    {
        // The state token is what ties this callback to an app deep link, so it is resolved
        // before anything else — without it there is nowhere to send the browser.
        if (string.IsNullOrEmpty(state))
        {
            return OAuthHandoffPage.Failed(Response,
                "That connection link looks incomplete.");
        }

        var appRedirectUri = await _deviceConnections.GetAppRedirectUriAsync(provider, state, ct);
        if (appRedirectUri is null)
        {
            return OAuthHandoffPage.Failed(Response,
                "That connection link has expired or has already been used.");
        }

        var query = new StringBuilder();
        query.Append(appRedirectUri.Contains('?') ? '&' : '?');
        query.Append("state=").Append(Uri.EscapeDataString(state));

        if (!string.IsNullOrEmpty(code))
        {
            query.Append("&code=").Append(Uri.EscapeDataString(code));
        }
        else
        {
            // A callback with neither code nor a named error is malformed rather than denied;
            // the app needs some error to react to either way.
            query.Append("&error=").Append(Uri.EscapeDataString(
                string.IsNullOrEmpty(error) ? "invalid_request" : error));
            if (!string.IsNullOrEmpty(errorDescription))
            {
                query.Append("&error_description=").Append(Uri.EscapeDataString(errorDescription));
            }

            Logger.LogInformation(
                "Device OAuth callback for {Provider} returned error {Error}", provider, error ?? "invalid_request");
        }

        return OAuthHandoffPage.Handoff(Response, appRedirectUri + query);
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
