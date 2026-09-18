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

/// <summary>
/// The caregiver's half of wearer-side device onboarding: mint an invitation, watch it, cancel it.
/// The wearer's half is <see cref="WearerConnectController"/>, which is anonymous.
/// </summary>
/// <remarks>
/// Separate from <see cref="DevicesController"/> despite sharing its route prefix, because the two
/// have different shapes of risk. That controller is the authenticated device lifecycle; this one
/// mints a credential that will be handed to somebody with no account, and is worth being able to
/// read, review and rate-limit on its own.
/// </remarks>
[Authorize]
[AuditHealthDataAccess("ManageDeviceInvite", EntityType = "DeviceConnectionInvite")]
[Route("api/v1")]
public class DeviceInvitesController : BaseApiController
{
    private readonly IDeviceConnectionInviteService _invites;
    private readonly IValidator<CreateDeviceInviteRequest> _createValidator;

    public DeviceInvitesController(
        IUserContext userContext,
        ILogger<DeviceInvitesController> logger,
        IDeviceConnectionInviteService invites,
        IValidator<CreateDeviceInviteRequest> createValidator)
        : base(userContext, logger)
    {
        _invites = invites;
        _createValidator = createValidator;
    }

    /// <summary>
    /// Creates an invitation and returns it with the one-time URL to hand the wearer. Supersedes any
    /// live invitation for the same member and brand.
    /// </summary>
    /// <remarks>
    /// The URL comes back exactly once, here. Neither the status read nor any later create returns
    /// it again: it is a live credential, and a response that kept re-issuing it would turn the
    /// waiting screen's poll into a repeated chance to leak one.
    /// </remarks>
    [HttpPost("cardimembers/{cardiMemberId:guid}/device-invites")]
    [ProducesResponseType(typeof(ApiResponse<DeviceInviteResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<DeviceInviteResponse>>> Create(
        Guid cardiMemberId, [FromBody] CreateDeviceInviteRequest request, CancellationToken ct)
    {
        if (!UserContext.IsAuthenticated || UserContext.UserId == Guid.Empty)
        {
            return Error("We couldn't find your account — please sign in again.", StatusCodes.Status403Forbidden);
        }

        var validation = await _createValidator.ValidateAsync(request, ct);
        if (!validation.IsValid)
        {
            return ValidationFailed(validation);
        }

        try
        {
            var invite = await _invites.CreateAsync(
                UserContext.UserId, cardiMemberId, request, RequestBaseUrl(), ct);

            return Created(invite, "Here's the link to send them.");
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
    /// One invitation's current state — what the caregiver's waiting screen polls while the wearer
    /// is deciding.
    /// </summary>
    [HttpGet("cardimembers/{cardiMemberId:guid}/device-invites/{inviteId:guid}")]
    [ProducesResponseType(typeof(ApiResponse<DeviceInviteResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<DeviceInviteResponse>>> Get(
        Guid cardiMemberId, Guid inviteId, CancellationToken ct)
    {
        if (!UserContext.IsAuthenticated || UserContext.UserId == Guid.Empty)
        {
            return Error("We couldn't find your account — please sign in again.", StatusCodes.Status403Forbidden);
        }

        try
        {
            return Success(await _invites.GetAsync(UserContext.UserId, cardiMemberId, inviteId, ct));
        }
        catch (KeyNotFoundException ex)
        {
            return Error(ex.Message, StatusCodes.Status404NotFound);
        }
    }

    /// <summary>
    /// Cancels an invitation. Returns its resulting state, which is <c>completed</c> rather than
    /// <c>revoked</c> when the wearer got there first — a cancel that lost a race has still left the
    /// caregiver where they wanted to be.
    /// </summary>
    [HttpDelete("cardimembers/{cardiMemberId:guid}/device-invites/{inviteId:guid}")]
    [ProducesResponseType(typeof(ApiResponse<DeviceInviteResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<DeviceInviteResponse>>> Revoke(
        Guid cardiMemberId, Guid inviteId, CancellationToken ct)
    {
        if (!UserContext.IsAuthenticated || UserContext.UserId == Guid.Empty)
        {
            return Error("We couldn't find your account — please sign in again.", StatusCodes.Status403Forbidden);
        }

        try
        {
            var invite = await _invites.RevokeAsync(UserContext.UserId, cardiMemberId, inviteId, ct);
            return Success(invite, "That link won't work any more.");
        }
        catch (KeyNotFoundException ex)
        {
            return Error(ex.Message, StatusCodes.Status404NotFound);
        }
    }

    /// <summary>
    /// This request's own origin, offered to the service as a fallback for building the wearer's URL
    /// when no public base is configured.
    /// </summary>
    /// <remarks>
    /// Only ever a fallback. <c>Host</c> is client-supplied and a deployed environment configures
    /// the base explicitly, so this is what a developer on localhost gets and not what anyone
    /// reachable from the internet does.
    /// </remarks>
    private string RequestBaseUrl() => $"{Request.Scheme}://{Request.Host}";
}
