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

    public InsightsController(
        IUserContext userContext,
        ILogger<InsightsController> logger,
        IHealthInsightService insightService)
        : base(userContext, logger)
    {
        _insightService = insightService;
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
}
