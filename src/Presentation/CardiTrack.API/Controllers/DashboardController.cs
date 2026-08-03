using CardiTrack.API.Infrastructure.UserContext;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Interfaces.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CardiTrack.API.Controllers;

[Authorize]
[Route("api/v1")]
public class DashboardController : BaseApiController
{
    private readonly IDashboardService _dashboardService;

    public DashboardController(
        IUserContext userContext,
        ILogger<DashboardController> logger,
        IDashboardService dashboardService)
        : base(userContext, logger)
    {
        _dashboardService = dashboardService;
    }

    /// <summary>Composed dashboard payload (M1-09) for one CardiMember.</summary>
    [HttpGet("cardimembers/{cardiMemberId:guid}/dashboard")]
    [ProducesResponseType(typeof(ApiResponse<DashboardResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<DashboardResponse>>> GetDashboard(
        Guid cardiMemberId, CancellationToken ct)
    {
        if (!UserContext.IsAuthenticated || UserContext.UserId == Guid.Empty)
        {
            return Error("User account not found", StatusCodes.Status403Forbidden);
        }

        try
        {
            var result = await _dashboardService.GetDashboardAsync(UserContext.UserId, cardiMemberId, ct);
            return Success(result);
        }
        catch (KeyNotFoundException ex)
        {
            return Error(ex.Message, StatusCodes.Status404NotFound);
        }
    }
}
