using CardiTrack.API.Infrastructure.UserContext;
using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Exceptions;
using CardiTrack.Application.Interfaces.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CardiTrack.API.Controllers;

/// <summary>
/// Families and the people in them: which ones you are in, who else is there, who runs each, and
/// how somebody leaves.
/// </summary>
/// <remarks>
/// No <c>[AuditHealthDataAccess]</c> on the class. Nothing here reads a reading, a alert or a
/// note — it is a roster of adults and the roles they hold. The endpoints that do touch a member's
/// data carry the attribute themselves, and spraying it over a membership list would fill the
/// HIPAA trail with rows that have no health data behind them.
/// </remarks>
[Authorize]
[Route("api/v1/families")]
public class FamiliesController : BaseApiController
{
    private readonly IFamilyService _families;

    public FamiliesController(
        IUserContext userContext,
        ILogger<FamiliesController> logger,
        IFamilyService families)
        : base(userContext, logger)
    {
        _families = families;
    }

    /// <summary>Every family you are in, with your role and what you can see in each.</summary>
    [HttpGet("mine")]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<FamilySummary>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<FamilySummary>>>> Mine(CancellationToken ct)
    {
        if (!UserContext.IsAuthenticated || UserContext.UserId == Guid.Empty)
        {
            return Error("We couldn't find your account — please sign in again.", StatusCodes.Status403Forbidden);
        }

        return Success(await _families.GetMineAsync(UserContext.UserId, ct));
    }

    /// <summary>Everyone in one family. Any member of it may read this.</summary>
    [HttpGet("{organizationId:guid}/members")]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<FamilyMemberSummary>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<FamilyMemberSummary>>>> Members(
        Guid organizationId, CancellationToken ct)
    {
        if (!UserContext.IsAuthenticated || UserContext.UserId == Guid.Empty)
        {
            return Error("We couldn't find your account — please sign in again.", StatusCodes.Status403Forbidden);
        }

        try
        {
            return Success(await _families.GetMembersAsync(UserContext.UserId, organizationId, ct));
        }
        catch (KeyNotFoundException ex)
        {
            return Error(ex.Message, StatusCodes.Status404NotFound);
        }
    }

    /// <summary>
    /// Hands this family, and its plan, to somebody else in it. You become a member in the same
    /// act — a family has one admin.
    /// </summary>
    [HttpPut("{organizationId:guid}/admin")]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<FamilyMemberSummary>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<FamilyMemberSummary>>>> TransferAdmin(
        Guid organizationId, [FromBody] TransferFamilyAdminRequest request, CancellationToken ct)
    {
        if (!UserContext.IsAuthenticated || UserContext.UserId == Guid.Empty)
        {
            return Error("We couldn't find your account — please sign in again.", StatusCodes.Status403Forbidden);
        }

        try
        {
            var roster = await _families.TransferAdminAsync(
                UserContext.UserId, organizationId, request.UserId, ct);
            return Success(roster, "They're the admin now.");
        }
        catch (KeyNotFoundException ex)
        {
            return Error(ex.Message, StatusCodes.Status404NotFound);
        }
    }

    /// <summary>Removes somebody from this family, and with it their view of its members.</summary>
    [HttpDelete("{organizationId:guid}/members/{userId:guid}")]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<object>>> RemoveMember(
        Guid organizationId, Guid userId, CancellationToken ct)
    {
        if (!UserContext.IsAuthenticated || UserContext.UserId == Guid.Empty)
        {
            return Error("We couldn't find your account — please sign in again.", StatusCodes.Status403Forbidden);
        }

        try
        {
            await _families.RemoveMemberAsync(UserContext.UserId, organizationId, userId, ct);
            return Success<object>(new { }, "They no longer have access.");
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

    /// <summary>Leaves a family you are in, giving up your view of its members.</summary>
    [HttpDelete("{organizationId:guid}/members/me")]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiResponse<object>>> Leave(Guid organizationId, CancellationToken ct)
    {
        if (!UserContext.IsAuthenticated || UserContext.UserId == Guid.Empty)
        {
            return Error("We couldn't find your account — please sign in again.", StatusCodes.Status403Forbidden);
        }

        try
        {
            await _families.LeaveAsync(UserContext.UserId, organizationId, ct);
            return Success<object>(new { }, "You've left the family.");
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
}
