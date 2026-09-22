using CardiTrack.API.Infrastructure.UserContext;
using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Exceptions;
using CardiTrack.Application.Interfaces.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CardiTrack.API.Controllers;

/// <summary>
/// The Family ID route in: somebody asks to join, an admin decides what they get.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Asking is rate-limited, and that is load-bearing.</strong> A Family ID is short enough
/// to read down the phone, so it is short enough to walk through. The product's defence is that
/// knowing one buys only the right to ask — approval is the access control — but a caller able to
/// ask thousands of times a minute would still learn the shape of the estate from timing alone.
/// The limit lives with every other one, in <c>IpRateLimiting</c> in appsettings
/// (<c>post:/api/v1/families/join-requests</c>), rather than in an attribute here: a rule nobody
/// can find beside its siblings is a rule nobody reviews. It and the deliberately uninformative
/// receipt are two halves of one answer.
/// </para>
/// <para>
/// No <c>[AuditHealthDataAccess]</c> on the class: nothing here reads health data. The approval
/// that grants access to a member does write membership and grant rows, and those are visible in
/// the family's own roster rather than in the HIPAA trail, which exists for reads of a person's
/// readings.
/// </para>
/// </remarks>
[Authorize]
[Route("api/v1/families")]
public class FamilyJoinController : BaseApiController
{
    private readonly IFamilyJoinService _join;

    public FamilyJoinController(
        IUserContext userContext,
        ILogger<FamilyJoinController> logger,
        IFamilyJoinService join)
        : base(userContext, logger)
    {
        _join = join;
    }

    /// <summary>Asks to join the family with this Family ID.</summary>
    /// <remarks>
    /// Always answers the same way. An unknown code, a malformed one, and a family the caller is
    /// already in are indistinguishable here — a walk through the code space has to learn nothing.
    /// </remarks>
    [HttpPost("join-requests")]
    [ProducesResponseType(typeof(ApiResponse<FamilyJoinRequestReceipt>), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    // Named Ask rather than Request: ControllerBase.Request is the incoming HttpRequest, and
    // shadowing it makes every later use of Request in this class ambiguous to the reader even
    // where it compiles.
    public async Task<ActionResult<ApiResponse<FamilyJoinRequestReceipt>>> Ask(
        [FromBody] JoinFamilyRequest request, CancellationToken ct)
    {
        if (!UserContext.IsAuthenticated || UserContext.UserId == Guid.Empty)
        {
            return Error("We couldn't find your account — please sign in again.", StatusCodes.Status403Forbidden);
        }

        var receipt = await _join.RequestAsync(UserContext.UserId, request, ct);
        return Success(receipt, "If that code is right, we've asked their admin. They'll let you know.");
    }

    /// <summary>What the caller is waiting on.</summary>
    [HttpGet("join-requests/mine")]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<FamilyJoinRequestSummary>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<FamilyJoinRequestSummary>>>> Mine(
        CancellationToken ct)
    {
        if (!UserContext.IsAuthenticated || UserContext.UserId == Guid.Empty)
        {
            return Error("We couldn't find your account — please sign in again.", StatusCodes.Status403Forbidden);
        }

        return Success(await _join.GetMineAsync(UserContext.UserId, ct));
    }

    /// <summary>The asker changes their mind.</summary>
    [HttpDelete("join-requests/{requestId:guid}")]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<object>>> Withdraw(Guid requestId, CancellationToken ct)
    {
        if (!UserContext.IsAuthenticated || UserContext.UserId == Guid.Empty)
        {
            return Error("We couldn't find your account — please sign in again.", StatusCodes.Status403Forbidden);
        }

        try
        {
            await _join.WithdrawAsync(UserContext.UserId, requestId, ct);
            return Success<object>(new { }, "We've withdrawn your request.");
        }
        catch (KeyNotFoundException ex)
        {
            return Error(ex.Message, StatusCodes.Status404NotFound);
        }
    }

    /// <summary>Who is waiting on this family's admin.</summary>
    [HttpGet("{organizationId:guid}/join-requests")]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<PendingJoinRequest>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<PendingJoinRequest>>>> Pending(
        Guid organizationId, CancellationToken ct)
    {
        if (!UserContext.IsAuthenticated || UserContext.UserId == Guid.Empty)
        {
            return Error("We couldn't find your account — please sign in again.", StatusCodes.Status403Forbidden);
        }

        try
        {
            return Success(await _join.GetPendingAsync(UserContext.UserId, organizationId, ct));
        }
        catch (KeyNotFoundException ex)
        {
            return Error(ex.Message, StatusCodes.Status404NotFound);
        }
    }

    /// <summary>Lets somebody in, with the members and role the admin chose.</summary>
    [HttpPost("{organizationId:guid}/join-requests/{requestId:guid}/approve")]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<object>>> Approve(
        Guid organizationId,
        Guid requestId,
        [FromBody] ApproveJoinRequest decision,
        CancellationToken ct)
    {
        if (!UserContext.IsAuthenticated || UserContext.UserId == Guid.Empty)
        {
            return Error("We couldn't find your account — please sign in again.", StatusCodes.Status403Forbidden);
        }

        try
        {
            await _join.ApproveAsync(UserContext.UserId, organizationId, requestId, decision, ct);
            return Success<object>(new { }, "They're in.");
        }
        catch (KeyNotFoundException ex)
        {
            return Error(ex.Message, StatusCodes.Status404NotFound);
        }
        catch (FamilyRuleException ex)
        {
            return Error(ex.Message, StatusCodes.Status422UnprocessableEntity);
        }
    }

    /// <summary>Says no.</summary>
    [HttpPost("{organizationId:guid}/join-requests/{requestId:guid}/decline")]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<object>>> Decline(
        Guid organizationId, Guid requestId, CancellationToken ct)
    {
        if (!UserContext.IsAuthenticated || UserContext.UserId == Guid.Empty)
        {
            return Error("We couldn't find your account — please sign in again.", StatusCodes.Status403Forbidden);
        }

        try
        {
            await _join.DeclineAsync(UserContext.UserId, organizationId, requestId, ct);
            return Success<object>(new { }, "We've let them know.");
        }
        catch (KeyNotFoundException ex)
        {
            return Error(ex.Message, StatusCodes.Status404NotFound);
        }
    }
}
