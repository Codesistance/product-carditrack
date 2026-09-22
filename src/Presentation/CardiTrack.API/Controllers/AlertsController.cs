using CardiTrack.API.Infrastructure.Auditing;
using CardiTrack.API.Infrastructure.UserContext;
using CardiTrack.Application.DTOs.Common;
using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Exceptions;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CardiTrack.API.Controllers;

/// <summary>
/// Alert retrieval, acknowledgment, and the detail payload for M1-11/12/16.
/// </summary>
[Authorize]
[Route("api/v1")]
public class AlertsController : BaseApiController
{
    private readonly IAlertService _alertService;

    public AlertsController(
        IUserContext userContext,
        ILogger<AlertsController> logger,
        IAlertService alertService)
        : base(userContext, logger)
    {
        _alertService = alertService;
    }

    /// <summary>Alerts across every CardiMember the caller may read.</summary>
    [HttpGet("alerts")]
    [AuditHealthDataAccess("ViewAlerts")]
    [ProducesResponseType(typeof(ApiResponse<AlertListResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public Task<ActionResult<ApiResponse<AlertListResponse>>> GetAlerts(
        [FromQuery] Guid? cardiMemberId,
        [FromQuery] string? severity,
        [FromQuery] string? status,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] int? limit,
        [FromQuery] int? offset,
        CancellationToken ct)
        => ListAsync(cardiMemberId, severity, status, from, to, limit, offset, ct);

    /// <summary>Alerts for one CardiMember. Same filters as <see cref="GetAlerts"/>.</summary>
    [HttpGet("cardimembers/{cardiMemberId:guid}/alerts")]
    [AuditHealthDataAccess("ViewAlerts")]
    [ProducesResponseType(typeof(ApiResponse<AlertListResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public Task<ActionResult<ApiResponse<AlertListResponse>>> GetAlertsForMember(
        Guid cardiMemberId,
        [FromQuery] string? severity,
        [FromQuery] string? status,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] int? limit,
        [FromQuery] int? offset,
        CancellationToken ct)
        => ListAsync(cardiMemberId, severity, status, from, to, limit, offset, ct);

    /// <summary>One alert for the mobile detail screen — the series is the metric that caused it.</summary>
    [HttpGet("alerts/{alertId:guid}")]
    [AuditHealthDataAccess("ViewAlert")]
    [ProducesResponseType(typeof(ApiResponse<AlertDetailResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<AlertDetailResponse>>> GetAlert(
        Guid alertId, CancellationToken ct)
    {
        if (!UserContext.IsAuthenticated || UserContext.UserId == Guid.Empty)
        {
            return Error("We couldn't find your account — please sign in again.", StatusCodes.Status403Forbidden);
        }

        try
        {
            var result = await _alertService.GetByIdAsync(UserContext.UserId, alertId, ct);
            return Success(result);
        }
        catch (KeyNotFoundException ex)
        {
            return Error(ex.Message, StatusCodes.Status404NotFound);
        }
    }

    /// <summary>
    /// Marks an alert as handled by the signed-in caregiver, optionally saying what they did.
    /// </summary>
    /// <remarks>
    /// The body is optional and so is every field in it — the bodiless form that shipped first
    /// still works and means the same thing. A <c>responseCode</c> must come from this alert's
    /// <c>responseOptions</c>; anything else is 400 naming the codes that would have been
    /// accepted, because a stale app needs to be told what to re-sync to rather than simply
    /// refused.
    /// </remarks>
    [HttpPost("alerts/{alertId:guid}/acknowledge")]
    [AuditHealthDataAccess("AcknowledgeAlert")]
    [ProducesResponseType(typeof(ApiResponse<AlertAcknowledgementResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<AlertAcknowledgementResponse>>> Acknowledge(
        Guid alertId, [FromBody] AlertAnswerRequest? request, CancellationToken ct)
    {
        if (!UserContext.IsAuthenticated || UserContext.UserId == Guid.Empty)
        {
            return Error("We couldn't find your account — please sign in again.", StatusCodes.Status403Forbidden);
        }

        try
        {
            var result = await _alertService.AcknowledgeAsync(
                UserContext.UserId, alertId, request?.ResponseCode, request?.Note, ct);
            return Success(result, "Marked as handled.");
        }
        catch (KeyNotFoundException ex)
        {
            return Error(ex.Message, StatusCodes.Status404NotFound);
        }
        catch (AlertResponseCodeException ex)
        {
            return Error(ex.Message);
        }
    }

    /// <summary>
    /// Closes an alert: the family has dealt with it, and the rule may fire again.
    /// </summary>
    /// <remarks>
    /// Deliberately has no <c>DELETE</c> counterpart. Acknowledging is a claim a caregiver can
    /// take back; closing says the episode is over, which is the same claim CardiTrack's own
    /// producers make when a condition passes — and it re-arms the rule, so a condition that has
    /// not really passed raises a fresh alert rather than being reopened by hand.
    /// </remarks>
    [HttpPost("alerts/{alertId:guid}/close")]
    [AuditHealthDataAccess("CloseAlert")]
    [ProducesResponseType(typeof(ApiResponse<AlertAcknowledgementResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<AlertAcknowledgementResponse>>> Close(
        Guid alertId, [FromBody] AlertAnswerRequest? request, CancellationToken ct)
    {
        if (!UserContext.IsAuthenticated || UserContext.UserId == Guid.Empty)
        {
            return Error("We couldn't find your account — please sign in again.", StatusCodes.Status403Forbidden);
        }

        try
        {
            var result = await _alertService.CloseAsync(
                UserContext.UserId, alertId, request?.ResponseCode, request?.Note, ct);
            return Success(result, "Closed.");
        }
        catch (KeyNotFoundException ex)
        {
            return Error(ex.Message, StatusCodes.Status404NotFound);
        }
        catch (AlertResponseCodeException ex)
        {
            return Error(ex.Message);
        }
    }

    /// <summary>
    /// Puts an acknowledged alert back to unhandled — the undo for
    /// <see cref="Acknowledge"/>, which a caregiver can otherwise only get wrong once.
    /// </summary>
    [HttpDelete("alerts/{alertId:guid}/acknowledge")]
    [AuditHealthDataAccess("UnacknowledgeAlert")]
    [ProducesResponseType(typeof(ApiResponse<AlertAcknowledgementResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<AlertAcknowledgementResponse>>> Unacknowledge(
        Guid alertId, CancellationToken ct)
    {
        if (!UserContext.IsAuthenticated || UserContext.UserId == Guid.Empty)
        {
            return Error("We couldn't find your account — please sign in again.", StatusCodes.Status403Forbidden);
        }

        try
        {
            var result = await _alertService.UnacknowledgeAsync(UserContext.UserId, alertId, ct);
            return Success(result, "Marked as not handled.");
        }
        catch (KeyNotFoundException ex)
        {
            return Error(ex.Message, StatusCodes.Status404NotFound);
        }
        catch (AlertStateException ex)
        {
            // A resolved alert. The service's own message explains why and is written to be read
            // by a caregiver — which is exactly why this catches its own exception type and not
            // InvalidOperationException: an incidental framework fault must surface as a 5xx, not
            // as advice.
            return Error(ex.Message);
        }
    }

    /// <summary>Removes an alert from the caregiver's own lists — housekeeping, not a clinical action.</summary>
    [HttpDelete("alerts/{alertId:guid}")]
    [AuditHealthDataAccess("DeleteAlert")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid alertId, CancellationToken ct)
    {
        if (!UserContext.IsAuthenticated || UserContext.UserId == Guid.Empty)
        {
            return Error("We couldn't find your account — please sign in again.", StatusCodes.Status403Forbidden);
        }

        try
        {
            await _alertService.DeleteAsync(UserContext.UserId, alertId, ct);
            return NoContent();
        }
        catch (KeyNotFoundException ex)
        {
            return Error(ex.Message, StatusCodes.Status404NotFound);
        }
    }

    private async Task<ActionResult<ApiResponse<AlertListResponse>>> ListAsync(
        Guid? cardiMemberId,
        string? severity,
        string? status,
        DateTime? from,
        DateTime? to,
        int? limit,
        int? offset,
        CancellationToken ct)
    {
        if (!UserContext.IsAuthenticated || UserContext.UserId == Guid.Empty)
        {
            return Error("We couldn't find your account — please sign in again.", StatusCodes.Status403Forbidden);
        }

        if (!TryParseFilter<AlertSeverity>(severity, out var parsedSeverity))
            return Error("That severity isn't one we recognise — use green, yellow, orange or red.");

        if (!TryParseFilter<AlertStatusFilter>(status, out var parsedStatus))
            return Error("That status isn't one we recognise — use new, acknowledged, resolved or open.");

        if (from is not null && to is not null && from > to)
            return Error("The start date needs to come before the end date.");

        try
        {
            var result = await _alertService.GetAlertsAsync(
                UserContext.UserId,
                cardiMemberId,
                parsedSeverity,
                parsedStatus,
                from,
                to,
                limit ?? AlertQuery.DefaultLimit,
                offset ?? 0,
                ct);
            return Success(result);
        }
        catch (KeyNotFoundException ex)
        {
            return Error(ex.Message, StatusCodes.Status404NotFound);
        }
    }

    /// <summary>
    /// Parses an optional lowercase filter value. A blank value means "no filter"; an
    /// unrecognised one is rejected rather than ignored, so a typo'd chip can't silently
    /// return the unfiltered list.
    /// </summary>
    private static bool TryParseFilter<TEnum>(string? value, out TEnum? parsed)
        where TEnum : struct, Enum
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(value))
            return true;

        if (!Enum.TryParse<TEnum>(value, ignoreCase: true, out var result) || !Enum.IsDefined(result))
            return false;

        parsed = result;
        return true;
    }
}
