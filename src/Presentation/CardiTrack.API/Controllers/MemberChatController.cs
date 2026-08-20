using System.Net;
using CardiTrack.API.Infrastructure.Auditing;
using CardiTrack.API.Infrastructure.UserContext;
using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Interfaces.Services;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CardiTrack.API.Controllers;

/// <summary>
/// A caregiver's persisted, multi-turn conversation about one CardiMember. Distinct from
/// <see cref="ChatController"/> (general provider, de-identified, client-replayed history) and from
/// <see cref="InsightsController"/>'s one-shot ask endpoint — see <see cref="IMemberChatService"/>.
/// </summary>
[Authorize]
[AuditHealthDataAccess("MemberChat")]
[Route("api/v1/member-chat")]
public class MemberChatController : BaseApiController
{
    private readonly IMemberChatService _chat;
    private readonly IValidator<MemberChatMessageRequest> _messageValidator;

    public MemberChatController(
        IUserContext userContext,
        ILogger<MemberChatController> logger,
        IMemberChatService chat,
        IValidator<MemberChatMessageRequest> messageValidator)
        : base(userContext, logger)
    {
        _chat = chat;
        _messageValidator = messageValidator;
    }

    /// <summary>
    /// Sends one message, auto-creating or continuing the caregiver's active session for this
    /// member — there is no separate "start session" call.
    /// </summary>
    [HttpPost("members/{cardiMemberId:guid}/messages")]
    [ProducesResponseType(typeof(ApiResponse<MemberChatMessageResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<ApiResponse<MemberChatMessageResponse>>> SendMessage(
        Guid cardiMemberId, [FromBody] MemberChatMessageRequest request, CancellationToken ct)
    {
        if (!UserContext.IsAuthenticated || UserContext.UserId == Guid.Empty)
        {
            return Error("We couldn't find your account — please sign in again.", StatusCodes.Status403Forbidden);
        }

        var validation = await _messageValidator.ValidateAsync(request, ct);
        if (!validation.IsValid)
            return ValidationFailed(validation);

        try
        {
            var result = await _chat.SendMessageAsync(UserContext.UserId, cardiMemberId, request.Message, ct);
            return Success(result);
        }
        catch (ArgumentException ex)
        {
            // The validator is the usual gate for an empty message; this also covers the
            // malicious/off-topic check's rejection, which has nothing else to map to.
            return Error(ex.Message, StatusCodes.Status400BadRequest);
        }
        catch (KeyNotFoundException ex)
        {
            return Error(ex.Message, StatusCodes.Status404NotFound);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
        {
            // The in-estate model host said it is full (MedGemmaClient's retries included) —
            // a capacity condition, not a fault in this request. 500 would page someone for a
            // queue; 503 tells the app, honestly, to ask again shortly.
            Logger.LogWarning(
                "Member chat send hit AI capacity for CardiMember {CardiMemberId}: upstream HTTP {StatusCode}",
                cardiMemberId, (int)ex.StatusCode!);
            return Error(
                "The assistant is busy catching up right now — give it a minute and ask again.",
                StatusCodes.Status503ServiceUnavailable);
        }
        catch (TimeoutException)
        {
            Logger.LogWarning(
                "Member chat send timed out against the AI host for CardiMember {CardiMemberId}",
                cardiMemberId);
            return Error(
                "The assistant is busy catching up right now — give it a minute and ask again.",
                StatusCodes.Status503ServiceUnavailable);
        }
    }

    /// <summary>The caregiver's active session for this member and its turns, for app-relaunch
    /// resume. 200 with a null <c>data</c> when no active session exists — not a 404, since the
    /// member itself may well exist and be viewable.</summary>
    [HttpGet("members/{cardiMemberId:guid}/sessions/current")]
    [ProducesResponseType(typeof(ApiResponse<MemberChatHistoryResponse?>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<MemberChatHistoryResponse?>>> GetCurrentSession(
        Guid cardiMemberId, CancellationToken ct)
    {
        if (!UserContext.IsAuthenticated || UserContext.UserId == Guid.Empty)
        {
            return Error("We couldn't find your account — please sign in again.", StatusCodes.Status403Forbidden);
        }

        try
        {
            var result = await _chat.GetCurrentSessionAsync(UserContext.UserId, cardiMemberId, ct);
            return Success(result);
        }
        catch (KeyNotFoundException ex)
        {
            return Error(ex.Message, StatusCodes.Status404NotFound);
        }
    }
}
