using CardiTrack.API.Infrastructure.UserContext;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Interfaces.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CardiTrack.API.Controllers;

/// <summary>
/// The assistant as a thing that has to be ready, rather than as a conversation —
/// <see cref="MemberChatController"/> owns the conversation itself.
/// </summary>
/// <remarks>
/// Its own controller, and not an action on that one, for two reasons. It is not about a member,
/// so it has no <c>cardiMemberId</c> to be routed under; and <see cref="MemberChatController"/>
/// carries <c>[AuditHealthDataAccess]</c>, which would file an app open as a health-record access
/// and put a row with nothing in it into the audit trail on every launch.
/// </remarks>
[Authorize]
[Route("api/v1/assistant")]
public class AssistantController : BaseApiController
{
    private readonly IAiWarmUpService _warmUp;

    public AssistantController(
        IUserContext userContext,
        ILogger<AssistantController> logger,
        IAiWarmUpService warmUp)
        : base(userContext, logger)
    {
        _warmUp = warmUp;
    }

    /// <summary>
    /// Says a caregiver has arrived, so the medical model can be loaded before they ask anything.
    /// Called at app startup, never waited on, and free to do nothing.
    /// </summary>
    /// <remarks>
    /// 202 rather than 200 because that is what actually happened: the load takes about a minute
    /// and this returns in microseconds, so the only honest answer is "noted". There is nothing
    /// to report back either way — a warm-up that was skipped as too recent and one that ran are
    /// the same answer to the app, which has no different behaviour available to it.
    /// </remarks>
    [HttpPost("prepare")]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    public ActionResult<ApiResponse<object>> Prepare()
    {
        if (!UserContext.IsAuthenticated || UserContext.UserId == Guid.Empty)
        {
            return Error("We couldn't find your account — please sign in again.", StatusCodes.Status403Forbidden);
        }

        _warmUp.RequestWarmUp();

        return Accepted(new ApiResponse<object>
        {
            Success = true,
            Message = "Getting the assistant ready.",
            Timestamp = DateTime.UtcNow,
        });
    }
}
