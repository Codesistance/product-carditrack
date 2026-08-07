using CardiTrack.API.Infrastructure.UserContext;
using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Interfaces.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CardiTrack.API.Controllers;

[Authorize]
[Route("api/v1/reports")]
public class ReportsController : BaseApiController
{
    private readonly IReportGenerationService _reportService;

    public ReportsController(
        IUserContext userContext,
        ILogger<ReportsController> logger,
        IReportGenerationService reportService)
        : base(userContext, logger)
    {
        _reportService = reportService;
    }

    /// <summary>Enqueue a report for generation. Returns 202 immediately with a report ID to poll.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(ApiResponse<ReportQueuedResponse>), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<ReportQueuedResponse>>> Generate(
        [FromBody] GenerateReportRequest request)
    {
        if (!UserContext.IsAuthenticated || UserContext.UserId == Guid.Empty)
        {
            return Error("We couldn't find your account — please sign in again.", StatusCodes.Status403Forbidden);
        }

        try
        {
            var result = await _reportService.GenerateAsync(UserContext.UserId, request);
            return Accepted(Success(result, "We're preparing your report — it'll be ready shortly!").Value);
        }
        catch (KeyNotFoundException ex)
        {
            return Error(ex.Message, StatusCodes.Status404NotFound);
        }
    }

    /// <summary>Get current status of a queued or completed report.</summary>
    [HttpGet("{reportId}")]
    [ProducesResponseType(typeof(ApiResponse<ReportStatusResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<ReportStatusResponse>>> GetStatus(string reportId)
    {
        if (!UserContext.IsAuthenticated || UserContext.UserId == Guid.Empty)
        {
            return Error("We couldn't find your account — please sign in again.", StatusCodes.Status403Forbidden);
        }

        var status = await _reportService.GetStatusAsync(UserContext.UserId, reportId);
        if (status is null)
            return Error("We couldn't find that report — it may have expired. Try generating a new one.", StatusCodes.Status404NotFound);

        return Success(status);
    }

    /// <summary>Download a completed report.</summary>
    [HttpGet("{reportId}/download")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Download(string reportId)
    {
        if (!UserContext.IsAuthenticated || UserContext.UserId == Guid.Empty)
        {
            return Error("We couldn't find your account — please sign in again.", StatusCodes.Status403Forbidden);
        }

        try
        {
            var (content, contentType, fileName) = await _reportService.DownloadAsync(UserContext.UserId, reportId);
            return File(content, contentType, fileName);
        }
        catch (KeyNotFoundException ex)
        {
            return Error(ex.Message, StatusCodes.Status404NotFound);
        }
        catch (InvalidOperationException ex)
        {
            return Error(ex.Message, StatusCodes.Status409Conflict);
        }
    }
}
