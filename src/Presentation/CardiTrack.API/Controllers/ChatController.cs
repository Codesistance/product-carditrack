using CardiTrack.API.Infrastructure.UserContext;
using CardiTrack.Application.DTOs.Responses;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CardiTrack.API.Controllers;

/// <summary>
/// Retired public chat. Caregiver questions about a member belong on
/// <see cref="MemberChatController"/>.
/// </summary>
[Authorize]
[Route("api/v1/chat")]
public class ChatController : BaseApiController
{
    public ChatController(IUserContext userContext, ILogger<ChatController> logger)
        : base(userContext, logger)
    {
    }

    /// <summary>
    /// Retired — send caregiver questions to
    /// <c>POST /api/v1/member-chat/members/{cardiMemberId}/messages</c>.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status410Gone)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    public ActionResult Chat()
    {
        if (!UserContext.IsAuthenticated || UserContext.UserId == Guid.Empty)
        {
            return Error("We couldn't find your account — please sign in again.", StatusCodes.Status403Forbidden);
        }

        return Error(
            "This chat endpoint is gone. Send questions about a member to "
            + "POST /api/v1/member-chat/members/{id}/messages.",
            StatusCodes.Status410Gone);
    }
}
