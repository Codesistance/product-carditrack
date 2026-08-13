using CardiTrack.API.Infrastructure.Auditing;
using CardiTrack.API.Infrastructure.UserContext;
using CardiTrack.Application.DTOs.Common;
using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CardiTrack.API.Controllers;

[Authorize]
[AuditHealthDataAccess("ChatAboutMember")]
[Route("api/v1/chat")]
public class ChatController : BaseApiController
{
    private readonly IGenerativeAiService _generativeAi;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICardiMemberAccessService _access;

    public ChatController(
        IUserContext userContext,
        ILogger<ChatController> logger,
        IGenerativeAiService generativeAi,
        IUnitOfWork unitOfWork,
        ICardiMemberAccessService access)
        : base(userContext, logger)
    {
        _generativeAi = generativeAi;
        _unitOfWork = unitOfWork;
        _access = access;
    }

    /// <summary>Send a message to the AI, with the CardiMember's recent health data injected as context.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(ApiResponse<ChatResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<ChatResponse>>> Chat(
        [FromBody] ChatRequest request, CancellationToken ct)
    {
        if (!UserContext.IsAuthenticated || UserContext.UserId == Guid.Empty)
        {
            return Error("We couldn't find your account — please sign in again.", StatusCodes.Status403Forbidden);
        }

        try
        {
            await _access.RequireViewAccessAsync(UserContext.UserId, request.CardiMemberId, ct);
        }
        catch (KeyNotFoundException ex)
        {
            return Error(ex.Message, StatusCodes.Status404NotFound);
        }

        var to = DateOnly.FromDateTime(DateTime.UtcNow);
        var from = to.AddDays(-3);
        var logs = await _unitOfWork.ActivityLogs
            .GetByCardiMemberAndDateRangeAsync(request.CardiMemberId, from, to);

        var systemContext = BuildSystemContext(logs);
        var history = request.History.Prepend(systemContext).ToList();

        var reply = await _generativeAi.ChatAsync(history, request.Message, ct);

        return Success(new ChatResponse
        {
            Reply = reply,
            ConversationId = Guid.NewGuid().ToString("N")
        });
    }

    /// <summary>
    /// Carries readings only. Chat goes to the general provider — today Gemini's consumer
    /// endpoint, outside the Google Cloud BAA — so nothing that identifies the member goes with
    /// them. The CardiMember id used to be included here and bought the model nothing: it cannot
    /// look the id up, and the caller already knows whose data they asked for.
    /// </summary>
    private static ChatMessage BuildSystemContext(
        IEnumerable<Domain.Entities.ActivityLog> logs)
    {
        var summary = logs.Any()
            ? string.Join(", ", logs.OrderBy(l => l.Date).Select(l =>
                $"{l.Date}: steps={l.Steps}, HR={l.RestingHeartRate}, sleep={l.SleepMinutes}min"))
            : "No recent activity data available.";

        return new ChatMessage
        {
            Role = ChatRole.User,
            Content = $"[System context] Patient's health data, last 3 days: {summary}. " +
                      "You are a helpful health assistant; answer questions about this data accurately and concisely."
        };
    }
}
