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
        catch (HttpRequestException ex)
        {
            // Everything HTTP inside this pipeline is a call to an in-estate model host, so any
            // HttpRequestException here means the assistant couldn't answer: saturation
            // (MedGemmaClient's retries exhausted on 429/503, StatusCode set), an unreachable
            // service (DNS/connection, StatusCode null), or a reply that couldn't be parsed.
            // None are a fault in this request — 500 would page someone for a queue; 503 tells
            // the app, honestly, to ask again shortly. MedGemmaClient's exception messages are
            // payload-free by design, so the exception itself is safe to log.
            Logger.LogWarning(ex,
                "Member chat send failed against the AI host for CardiMember {CardiMemberId} (upstream status {StatusCode})",
                cardiMemberId, ex.StatusCode);
            return Error(
                "The assistant is busy catching up right now — give it a minute and ask again.",
                StatusCodes.Status503ServiceUnavailable);
        }
        catch (TimeoutException ex)
        {
            Logger.LogWarning(ex,
                "Member chat send timed out against the AI host for CardiMember {CardiMemberId}",
                cardiMemberId);
            return Error(
                "The assistant is busy catching up right now — give it a minute and ask again.",
                StatusCodes.Status503ServiceUnavailable);
        }
    }

    /// <summary>
    /// Short lines for the app to cycle in the pending reply bubble while the send for the same
    /// message is in flight — called alongside <see cref="SendMessage"/>, never instead of it.
    /// Always 200 with sentences on a viewable member: generation failures come back as the
    /// service's canned lines, because waiting copy must never make the send look broken.
    /// </summary>
    [HttpPost("members/{cardiMemberId:guid}/waiting-sentences")]
    [ProducesResponseType(typeof(ApiResponse<MemberChatWaitingResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<MemberChatWaitingResponse>>> GetWaitingSentences(
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
            var sentences = await _chat.GetWaitingSentencesAsync(
                UserContext.UserId, cardiMemberId, request.Message, ct);
            return Success(new MemberChatWaitingResponse { Sentences = sentences });
        }
        catch (KeyNotFoundException ex)
        {
            return Error(ex.Message, StatusCodes.Status404NotFound);
        }
    }

    /// <summary>Question chips for the chat's empty state — deterministic from the member's data
    /// state (an unresolved alert earns an alert chip), no model call, instant.</summary>
    [HttpGet("members/{cardiMemberId:guid}/suggestions")]
    [ProducesResponseType(typeof(ApiResponse<MemberChatSuggestionsResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<MemberChatSuggestionsResponse>>> GetSuggestions(
        Guid cardiMemberId, CancellationToken ct)
    {
        if (!UserContext.IsAuthenticated || UserContext.UserId == Guid.Empty)
        {
            return Error("We couldn't find your account — please sign in again.", StatusCodes.Status403Forbidden);
        }

        try
        {
            var result = await _chat.GetSuggestionsAsync(UserContext.UserId, cardiMemberId, ct);
            return Success(result);
        }
        catch (KeyNotFoundException ex)
        {
            return Error(ex.Message, StatusCodes.Status404NotFound);
        }
    }

    /// <summary>Every conversation the caregiver has had about this member, newest activity
    /// first — the chat sheet's history list. Always 200 on a viewable member: an empty list is a
    /// caregiver who hasn't chatted yet, not an error.</summary>
    [HttpGet("members/{cardiMemberId:guid}/sessions")]
    [ProducesResponseType(typeof(ApiResponse<MemberChatSessionListResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<MemberChatSessionListResponse>>> GetSessions(
        Guid cardiMemberId, CancellationToken ct)
    {
        if (!UserContext.IsAuthenticated || UserContext.UserId == Guid.Empty)
        {
            return Error("We couldn't find your account — please sign in again.", StatusCodes.Status403Forbidden);
        }

        try
        {
            var result = await _chat.GetSessionsAsync(UserContext.UserId, cardiMemberId, ct);
            return Success(result);
        }
        catch (KeyNotFoundException ex)
        {
            return Error(ex.Message, StatusCodes.Status404NotFound);
        }
    }

    /// <summary>One conversation and its turns, read back for the history list. 404 covers the
    /// member and the session alike — a session that exists but is not this caregiver's own
    /// conversation about this member is indistinguishable from one that never existed.</summary>
    [HttpGet("members/{cardiMemberId:guid}/sessions/{sessionId:guid}")]
    [ProducesResponseType(typeof(ApiResponse<MemberChatHistoryResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<MemberChatHistoryResponse>>> GetSession(
        Guid cardiMemberId, Guid sessionId, CancellationToken ct)
    {
        if (!UserContext.IsAuthenticated || UserContext.UserId == Guid.Empty)
        {
            return Error("We couldn't find your account — please sign in again.", StatusCodes.Status403Forbidden);
        }

        try
        {
            var result = await _chat.GetSessionAsync(UserContext.UserId, cardiMemberId, sessionId, ct);
            return Success(result);
        }
        catch (KeyNotFoundException ex)
        {
            return Error(ex.Message, StatusCodes.Status404NotFound);
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
