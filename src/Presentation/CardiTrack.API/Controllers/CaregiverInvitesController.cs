using CardiTrack.API.Infrastructure.Auditing;
using CardiTrack.API.Infrastructure.UserContext;
using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Interfaces.Services;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CardiTrack.API.Controllers;

/// <summary>
/// The admin's half of adding a second caregiver: mint an invitation, watch it, cancel it — plus
/// the invitee's half, which needs an account and so is authorized like any other endpoint.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not folded into <see cref="CardiMembersController"/>, for the same reason
/// <see cref="DeviceInvitesController"/> sits apart from <see cref="DevicesController"/>: this
/// mints a credential that will be handed to somebody who may not have an account yet, and is
/// worth being able to read, review and rate-limit on its own.
/// </para>
/// <para>
/// The landing endpoints — <c>view</c> and <c>decline</c> — are the only ones a stranger holding a
/// token can reach. They return two first names and a deadline, and nothing else; the redemption
/// that actually grants anything demands a signed-in user.
/// </para>
/// </remarks>
[Authorize]
[AuditHealthDataAccess("ManageCaregiverInvite", EntityType = "CaregiverInvite")]
[Route("api/v1")]
public class CaregiverInvitesController : BaseApiController
{
    private readonly ICaregiverInviteService _invites;
    private readonly IValidator<CreateCaregiverInviteRequest> _createValidator;

    public CaregiverInvitesController(
        IUserContext userContext,
        ILogger<CaregiverInvitesController> logger,
        ICaregiverInviteService invites,
        IValidator<CreateCaregiverInviteRequest> createValidator)
        : base(userContext, logger)
    {
        _invites = invites;
        _createValidator = createValidator;
    }

    /// <summary>
    /// Creates an invitation for one member and returns it with the one-time URL to hand over.
    /// </summary>
    /// <remarks>
    /// The URL comes back exactly once, here. Neither the list nor any later create returns it
    /// again: it is a live credential, and a response that kept re-issuing it would turn every
    /// refresh of the caregiver list into another chance to leak one.
    /// </remarks>
    [HttpPost("cardimembers/{cardiMemberId:guid}/caregiver-invites")]
    [ProducesResponseType(typeof(ApiResponse<CaregiverInviteResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<CaregiverInviteResponse>>> Create(
        Guid cardiMemberId, [FromBody] CreateCaregiverInviteRequest request, CancellationToken ct)
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
    }

    /// <summary>Every invitation issued for this member, newest first.</summary>
    [HttpGet("cardimembers/{cardiMemberId:guid}/caregiver-invites")]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<CaregiverInviteResponse>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<CaregiverInviteResponse>>>> List(
        Guid cardiMemberId, CancellationToken ct)
    {
        if (!UserContext.IsAuthenticated || UserContext.UserId == Guid.Empty)
        {
            return Error("We couldn't find your account — please sign in again.", StatusCodes.Status403Forbidden);
        }

        try
        {
            return Success(await _invites.ListAsync(UserContext.UserId, cardiMemberId, ct));
        }
        catch (KeyNotFoundException ex)
        {
            return Error(ex.Message, StatusCodes.Status404NotFound);
        }
    }

    /// <summary>Withdraws a live invitation. Safe to call on one that is already finished.</summary>
    [HttpDelete("cardimembers/{cardiMemberId:guid}/caregiver-invites/{inviteId:guid}")]
    [ProducesResponseType(typeof(ApiResponse<CaregiverInviteResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<CaregiverInviteResponse>>> Revoke(
        Guid cardiMemberId, Guid inviteId, CancellationToken ct)
    {
        if (!UserContext.IsAuthenticated || UserContext.UserId == Guid.Empty)
        {
            return Error("We couldn't find your account — please sign in again.", StatusCodes.Status403Forbidden);
        }

        try
        {
            var invite = await _invites.RevokeAsync(UserContext.UserId, cardiMemberId, inviteId, ct);
            return Success(invite, "That invitation has been cancelled.");
        }
        catch (KeyNotFoundException ex)
        {
            return Error(ex.Message, StatusCodes.Status404NotFound);
        }
    }

    /// <summary>
    /// What the landing page may show for a token: who is asking, who they are asking about, and
    /// when the invitation stops working.
    /// </summary>
    /// <remarks>
    /// Authorized, unlike the wearer's equivalent. The invitee has to sign in before they can
    /// accept anyway, so there is nothing to gain by serving this to an anonymous caller and a
    /// first name to lose. An unknown, expired, spent or withdrawn token is a 404 either way —
    /// the cases are not distinguishable to somebody holding a guess.
    /// </remarks>
    [HttpGet("caregiver-invites/{token}")]
    [ProducesResponseType(typeof(ApiResponse<CaregiverInviteView>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<CaregiverInviteView>>> View(string token, CancellationToken ct)
    {
        var view = await _invites.ViewAsync(token, ct);
        return view is null
            ? Error("That invitation is no longer available.", StatusCodes.Status404NotFound)
            : Success(view);
    }

    /// <summary>
    /// Redeems an invitation for the signed-in user: a membership of the family and a grant on the
    /// member.
    /// </summary>
    [HttpPost("caregiver-invites/{token}/accept")]
    [ProducesResponseType(typeof(ApiResponse<CaregiverInviteRedemption>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<CaregiverInviteRedemption>>> Accept(
        string token, CancellationToken ct)
    {
        if (!UserContext.IsAuthenticated || UserContext.UserId == Guid.Empty)
        {
            return Error("Please sign in to accept this invitation.", StatusCodes.Status403Forbidden);
        }

        try
        {
            var redemption = await _invites.RedeemAsync(token, UserContext.UserId, ct);
            return Success(redemption, redemption.AlreadyHadAccess
                ? "You already had access to them."
                : "You can now see how they're doing.");
        }
        catch (KeyNotFoundException ex)
        {
            return Error(ex.Message, StatusCodes.Status404NotFound);
        }
    }

    /// <summary>Records that the invitee said no.</summary>
    [HttpPost("caregiver-invites/{token}/decline")]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<object>>> Decline(string token, CancellationToken ct)
    {
        return await _invites.DeclineAsync(token, ct)
            ? Success<object>(new { }, "Thanks for letting them know.")
            : Error("That invitation is no longer available.", StatusCodes.Status404NotFound);
    }

    /// <summary>
    /// The origin this request arrived on, used only when no public base URL is configured.
    /// </summary>
    /// <remarks>
    /// A Host header is attacker-controlled, so this is a development convenience and not the
    /// production path: every deployed environment sets
    /// <c>CaregiverInvites:PublicBaseUrl</c> explicitly, and dev's Cloud Armor policy refuses
    /// requests whose Host is not the configured domain. The same reasoning as
    /// <see cref="DeviceInvitesController"/>'s copy.
    /// </remarks>
    private string RequestBaseUrl() => $"{Request.Scheme}://{Request.Host}";
}
