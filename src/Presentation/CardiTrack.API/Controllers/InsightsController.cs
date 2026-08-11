using CardiTrack.API.Infrastructure.Auditing;
using CardiTrack.API.Infrastructure.UserContext;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Interfaces.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CardiTrack.API.Controllers;

[Authorize]
[AuditHealthDataAccess("ViewInsight")]
[Route("api/v1/insights")]
public class InsightsController : BaseApiController
{
    private readonly IHealthInsightService _insightService;
    private readonly IDigestQueryService _digests;

    public InsightsController(
        IUserContext userContext,
        ILogger<InsightsController> logger,
        IHealthInsightService insightService,
        IDigestQueryService digests)
        : base(userContext, logger)
    {
        _insightService = insightService;
        _digests = digests;
    }

    /// <summary>Analyse a specific alert using MedGemma.</summary>
    [HttpGet("alerts/{alertId:guid}")]
    [ProducesResponseType(typeof(ApiResponse<AlertInsightResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<AlertInsightResponse>>> AnalyzeAlert(
        Guid alertId, CancellationToken ct)
    {
        if (!UserContext.IsAuthenticated || UserContext.UserId == Guid.Empty)
        {
            return Error("We couldn't find your account — please sign in again.", StatusCodes.Status403Forbidden);
        }

        try
        {
            var result = await _insightService.AnalyzeAlertAsync(UserContext.UserId, alertId, ct);
            return Success(result);
        }
        catch (KeyNotFoundException ex)
        {
            return Error(ex.Message, StatusCodes.Status404NotFound);
        }
    }

    /// <summary>Analyse health baseline trends for a CardiMember using MedGemma.</summary>
    [HttpGet("members/{cardiMemberId:guid}/baseline")]
    [ProducesResponseType(typeof(ApiResponse<BaselineInsightResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<BaselineInsightResponse>>> AnalyzeBaseline(
        Guid cardiMemberId, CancellationToken ct)
    {
        if (!UserContext.IsAuthenticated || UserContext.UserId == Guid.Empty)
        {
            return Error("We couldn't find your account — please sign in again.", StatusCodes.Status403Forbidden);
        }

        try
        {
            var result = await _insightService.AnalyzeBaselineAsync(UserContext.UserId, cardiMemberId, ct);
            return Success(result);
        }
        catch (KeyNotFoundException ex)
        {
            return Error(ex.Message, StatusCodes.Status404NotFound);
        }
    }

    /// <summary>
    /// A short, empathetic line describing a CardiMember's current status — what the Dashboard's
    /// hero card shows once it resolves, in place of the fixed per-severity-tier copy the client
    /// renders while this is in flight. Cached per member; see
    /// <see cref="IHealthInsightService.GetCurrentStatusMessageAsync"/> for the TTL. A null
    /// <c>message</c> means there's nothing to say yet — the client keeps its existing copy.
    /// </summary>
    [HttpGet("members/{cardiMemberId:guid}/status")]
    [ProducesResponseType(typeof(ApiResponse<CurrentStatusMessageResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<CurrentStatusMessageResponse>>> GetCurrentStatus(
        Guid cardiMemberId, CancellationToken ct)
    {
        if (!UserContext.IsAuthenticated || UserContext.UserId == Guid.Empty)
        {
            return Error("We couldn't find your account — please sign in again.", StatusCodes.Status403Forbidden);
        }

        try
        {
            var result = await _insightService.GetCurrentStatusMessageAsync(UserContext.UserId, cardiMemberId, ct);
            return Success(result);
        }
        catch (KeyNotFoundException ex)
        {
            return Error(ex.Message, StatusCodes.Status404NotFound);
        }
    }

    /// <summary>
    /// The member's daily family digest — the most recent by default, or a specific local date.
    /// Generated each morning by the pipeline; read-only here, no model call on this path.
    /// </summary>
    [HttpGet("members/{cardiMemberId:guid}/digest")]
    [ProducesResponseType(typeof(ApiResponse<DigestResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<DigestResponse>>> GetDigest(
        Guid cardiMemberId, [FromQuery] DateOnly? date, CancellationToken ct)
    {
        if (!UserContext.IsAuthenticated || UserContext.UserId == Guid.Empty)
        {
            return Error("We couldn't find your account — please sign in again.", StatusCodes.Status403Forbidden);
        }

        try
        {
            var result = await _digests.GetDigestAsync(UserContext.UserId, cardiMemberId, date, ct);
            return result is null
                ? Error("No digest has been generated for this member yet.", StatusCodes.Status404NotFound)
                : Success(result);
        }
        catch (KeyNotFoundException ex)
        {
            return Error(ex.Message, StatusCodes.Status404NotFound);
        }
    }
}
