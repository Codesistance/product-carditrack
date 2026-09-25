using System.Text.Json;
using System.Threading.Channels;
using CardiTrack.API.Infrastructure.Auditing;
using CardiTrack.API.Infrastructure.Streaming;
using CardiTrack.API.Infrastructure.UserContext;
using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Infrastructure.Settings;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace CardiTrack.API.Controllers;

/// <summary>
/// A caregiver's persisted, multi-turn conversation about one CardiMember. Distinct from
/// <see cref="InsightsController"/>'s one-shot ask endpoint — see <see cref="IMemberChatService"/>.
/// <see cref="ChatController"/> is retired (410) and points here.
/// </summary>
[Authorize]
[AuditHealthDataAccess("MemberChat")]
[Route("api/v1/member-chat")]
public class MemberChatController : BaseApiController
{
    private readonly IMemberChatService _chat;
    private readonly IValidator<MemberChatMessageRequest> _messageValidator;
    private readonly TimeSpan _sendBudget;
    private readonly JsonSerializerOptions _json;

    public MemberChatController(
        IUserContext userContext,
        ILogger<MemberChatController> logger,
        IMemberChatService chat,
        IValidator<MemberChatMessageRequest> messageValidator,
        IOptions<MemberChatOptions> options,
        IOptions<JsonOptions>? jsonOptions = null)
        : base(userContext, logger)
    {
        _chat = chat;
        _messageValidator = messageValidator;
        _sendBudget = TimeSpan.FromSeconds(options.Value.SendBudgetSeconds);
        // The stream's events are serialised with the options MVC uses for every JSON response,
        // so an answer event reads exactly like the JSON endpoint's data.
        _json = jsonOptions?.Value.JsonSerializerOptions ?? new JsonSerializerOptions(JsonSerializerDefaults.Web);
    }

    /// <summary>
    /// Sends one message, auto-creating or continuing the caregiver's active session for this
    /// member — there is no separate "start session" call. The whole reply in one response; the
    /// streaming twin is <see cref="StreamMessage"/>. Kept for app builds that predate it.
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

        // One budget for the whole send (MemberChatOptions.SendBudgetSeconds). Every call inside
        // it has its own ceiling, but a chain of them can still outlast Cloud Run's request timeout
        // and end as a bare 504 with the work abandoned mid-write; this ends it here first, where
        // it rolls back cleanly and answers the same 503 a saturated model host does.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(_sendBudget);

        try
        {
            var result = await _chat.SendMessageAsync(UserContext.UserId, cardiMemberId, request.Message, budget.Token);
            NameAuditAction(result);
            return Success(result);
        }
        catch (Exception ex) when (MapSendFailure(ex, cardiMemberId, budget, ct) is { } failure)
        {
            return Error(failure.Message, failure.StatusCode);
        }
    }

    /// <summary>
    /// <see cref="SendMessage"/> as a stream of server-sent events: a <c>step</c> event as each
    /// stage of the pipeline starts, then one <c>answer</c> carrying the saved reply (the same
    /// <see cref="MemberChatMessageResponse"/> the JSON endpoint returns), then <c>done</c>. A
    /// failure after the stream has started ends it with one <c>error</c> event carrying the
    /// status and message the JSON endpoint would have answered with.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing is written until the first event, and the service reports its first step only once
    /// the malicious pre-check has passed — so the failures that have their own status (403, 400
    /// for validation or refusal, 404 for access) still arrive as ordinary JSON error responses,
    /// exactly as from <see cref="SendMessage"/>. Only failures later in the pipeline (a model
    /// host that is saturated, the send budget running out) can arrive as an <c>error</c> event.
    /// </para>
    /// <para>
    /// The pipeline and the writer are decoupled through a channel: the service reports steps
    /// synchronously and never waits on the network, and a slow reader can only delay its own
    /// events. A comment line goes out every <see cref="HeartbeatInterval"/> while nothing else
    /// does, so a proxy or a mobile network does not close a connection that sits silent through
    /// a long clinical read.
    /// </para>
    /// </remarks>
    [HttpPost("members/{cardiMemberId:guid}/messages/stream")]
    // The event-stream type is declared on the 200 alone, never with [Produces]: that filter
    // would stamp it on the JSON error results too, and with no formatter for it MVC would
    // answer those 400/404/503s as 406 Not Acceptable. The writer sets it when the stream starts.
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK, ServerSentEventWriter.ContentType)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> StreamMessage(
        Guid cardiMemberId, [FromBody] MemberChatMessageRequest request, CancellationToken ct)
    {
        if (!UserContext.IsAuthenticated || UserContext.UserId == Guid.Empty)
        {
            return Error("We couldn't find your account — please sign in again.", StatusCodes.Status403Forbidden);
        }

        var validation = await _messageValidator.ValidateAsync(request, ct);
        if (!validation.IsValid)
            return ValidationFailed(validation);

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(_sendBudget);

        var steps = Channel.CreateUnbounded<MemberChatStep>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        var progress = new ChannelProgress(steps.Writer);

        var send = SendThenCompleteAsync(cardiMemberId, request.Message, progress, steps.Writer, budget.Token);
        var events = new ServerSentEventWriter(Response, _json);

        try
        {
            while (await WaitForStepAsync(steps.Reader, ct))
            {
                while (steps.Reader.TryRead(out var step))
                    await events.WriteAsync("step", step, ct);
            }
        }
        catch
        {
            // The stream broke before the send settled: the caller hung up (the budget token is
            // linked to theirs, so the send is already being cancelled and rolls back) or a write
            // failed on a dead connection (the send carries on and saves, so the reply is in the
            // history when the app next loads it). Either way, wait for the send so the request
            // scope it runs in is not disposed under it, then let the failure surface — but a
            // send that saved an alert-settings or journal change is still named for the audit
            // trail: the change happened whether or not the caller saw the reply.
            await send.ContinueWith(_ => { }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            if (send.IsCompletedSuccessfully)
                NameAuditAction(send.Result);
            throw;
        }

        // The heartbeat loop above ends when the writer completes — the send has settled.
        MemberChatMessageResponse result;
        try
        {
            result = await send;
        }
        catch (Exception ex) when (MapSendFailure(ex, cardiMemberId, budget, ct) is { } failure)
        {
            if (!events.Started)
                return Error(failure.Message, failure.StatusCode);

            await events.WriteAsync("error", new StreamError { Status = failure.StatusCode, Message = failure.Message }, ct);
            return new EmptyResult();
        }
        catch when (events.Started)
        {
            // An unexpected fault after the 200 went out. The exception middleware still logs it,
            // but can no longer write its 500 body; this is that body, as the stream's last event,
            // so the app shows the same message rather than a reply that was cut off.
            await events.WriteAsync("error", new StreamError
            {
                Status = StatusCodes.Status500InternalServerError,
                Message = "Something went wrong on our end. Please try again in a moment.",
            }, ct);
            throw;
        }

        NameAuditAction(result);
        await events.WriteAsync("answer", result, ct);
        await events.WriteAsync("done", new { }, ct);
        return new EmptyResult();

        // Waits for the next step, writing a heartbeat for every interval that passes without
        // one — but only once the stream has started: before the first step there is nothing to
        // keep alive, and a heartbeat would commit the 200 a pre-check failure still needs to
        // be able to replace.
        async Task<bool> WaitForStepAsync(ChannelReader<MemberChatStep> reader, CancellationToken token)
        {
            // One wait for the whole call, however many heartbeats pass: a single-reader channel
            // holds one waiter, and a fresh wait per heartbeat would stack them.
            var ready = reader.WaitToReadAsync(token).AsTask();
            while (true)
            {
                var finished = await Task.WhenAny(ready, Task.Delay(HeartbeatInterval, token));
                if (finished == ready)
                    return await ready;
                token.ThrowIfCancellationRequested();
                if (events.Started)
                    await events.WriteHeartbeatAsync(token);
            }
        }
    }

    /// <summary>How long a stream may sit without a byte before a comment line keeps it open.
    /// Well inside the idle limits of common proxies and carrier NAT (30–60 s).</summary>
    internal static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

    private async Task<MemberChatMessageResponse> SendThenCompleteAsync(
        Guid cardiMemberId, string message, IProgress<MemberChatStep> progress,
        ChannelWriter<MemberChatStep> writer, CancellationToken ct)
    {
        try
        {
            return await _chat.SendMessageAsync(UserContext.UserId, cardiMemberId, message, progress, ct);
        }
        finally
        {
            writer.TryComplete();
        }
    }

    /// <summary>A send that applied an alert-settings change, or changed a CardiJournal book, is a
    /// write to the member's record, and the audit trail files it as that rather than as one more
    /// chat read.</summary>
    private void NameAuditAction(MemberChatMessageResponse result)
    {
        if (result.ChangedAlertSettings)
            HttpContext.Items[AuditHealthDataAccessAttribute.ActionItemKey] = "ChangeAlertSettingsViaChat";

        if (result.ChangedJournal)
            HttpContext.Items[AuditHealthDataAccessAttribute.ActionItemKey] = "ChangeJournalViaChat";
    }

    /// <summary>
    /// The status and message a failed send answers with, shared by both send endpoints so the
    /// JSON reply and the stream's <c>error</c> event cannot disagree. Null for anything else,
    /// which then propagates to the exception middleware as before — including the caller's own
    /// cancellation, which is theirs to see, not a server-side timeout to excuse.
    /// </summary>
    private SendFailure? MapSendFailure(
        Exception ex, Guid cardiMemberId, CancellationTokenSource budget, CancellationToken ct)
    {
        switch (ex)
        {
            case ArgumentException:
                // The validator is the usual gate for an empty message; this also covers the
                // malicious/off-topic check's rejection, which has nothing else to map to.
                return new SendFailure(StatusCodes.Status400BadRequest, ex.Message);

            case KeyNotFoundException:
                return new SendFailure(StatusCodes.Status404NotFound, ex.Message);

            case HttpRequestException http:
                // Everything HTTP inside this pipeline is a call to an in-estate model host, so any
                // HttpRequestException here means the assistant couldn't answer: saturation
                // (MedGemmaClient's retries exhausted on 429/503, StatusCode set), an unreachable
                // service (DNS/connection, StatusCode null), or a reply that couldn't be parsed.
                // None are a fault in this request — 500 would page someone for a queue; 503 tells
                // the app, honestly, to ask again shortly. MedGemmaClient's exception messages are
                // payload-free by design, so the exception itself is safe to log.
                Logger.LogWarning(ex,
                    "Member chat send failed against the AI host for CardiMember {CardiMemberId} (upstream status {StatusCode})",
                    cardiMemberId, http.StatusCode);
                return Busy();

            case TimeoutException:
                Logger.LogWarning(ex,
                    "Member chat send timed out against the AI host for CardiMember {CardiMemberId}",
                    cardiMemberId);
                return Busy();

            case OperationCanceledException when budget.IsCancellationRequested && !ct.IsCancellationRequested:
                Logger.LogWarning(ex,
                    "Member chat send for CardiMember {CardiMemberId} ran past its {BudgetSeconds}s budget",
                    cardiMemberId, _sendBudget.TotalSeconds);
                return Busy();

            default:
                return null;
        }

        static SendFailure Busy() => new(
            StatusCodes.Status503ServiceUnavailable,
            "The assistant is busy catching up right now — give it a minute and ask again.");
    }

    private sealed record SendFailure(int StatusCode, string Message);

    /// <summary>The <c>error</c> event's payload.</summary>
    private sealed class StreamError
    {
        public required int Status { get; init; }
        public required string Message { get; init; }
    }

    /// <summary>Reports straight into the channel: never blocks the pipeline, and a report after
    /// the send has settled is dropped rather than thrown.</summary>
    private sealed class ChannelProgress(ChannelWriter<MemberChatStep> writer) : IProgress<MemberChatStep>
    {
        public void Report(MemberChatStep value) => writer.TryWrite(value);
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

    /// <summary>The caregiver's completed conversations about this member, newest started
    /// first — the chat sheet's history list. The active conversation is never in it. Always 200
    /// on a viewable member: an empty list is a caregiver who hasn't chatted yet, not an
    /// error.</summary>
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

    /// <summary>Ends the caregiver's active conversation about this member — their next message
    /// starts fresh, and the ended conversation appears in the history list at once. 200 with a
    /// null <c>endedSessionId</c> when nothing was active: the caregiver asked for a fresh start
    /// and has one either way.</summary>
    [HttpPost("members/{cardiMemberId:guid}/sessions/current/end")]
    [ProducesResponseType(typeof(ApiResponse<MemberChatEndSessionResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<MemberChatEndSessionResponse>>> EndCurrentSession(
        Guid cardiMemberId, CancellationToken ct)
    {
        if (!UserContext.IsAuthenticated || UserContext.UserId == Guid.Empty)
        {
            return Error("We couldn't find your account — please sign in again.", StatusCodes.Status403Forbidden);
        }

        try
        {
            var result = await _chat.EndCurrentSessionAsync(UserContext.UserId, cardiMemberId, ct);
            return Success(result);
        }
        catch (KeyNotFoundException ex)
        {
            return Error(ex.Message, StatusCodes.Status404NotFound);
        }
    }

    /// <summary>Permanently deletes conversations from the caregiver's history about this
    /// member — the sessions, their turns and their usage rows; the client warns that this
    /// cannot be undone before calling. Ids that do not exist, or are not this caregiver's own
    /// conversations about this member, are skipped rather than failing the batch, and
    /// <c>deletedCount</c> says how many actually went. POST rather than DELETE because the ids
    /// travel as a body — the history list offers multi-select.</summary>
    [HttpPost("members/{cardiMemberId:guid}/sessions/delete")]
    [ProducesResponseType(typeof(ApiResponse<MemberChatDeleteSessionsResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<MemberChatDeleteSessionsResponse>>> DeleteSessions(
        Guid cardiMemberId, [FromBody] MemberChatDeleteSessionsRequest request, CancellationToken ct)
    {
        if (!UserContext.IsAuthenticated || UserContext.UserId == Guid.Empty)
        {
            return Error("We couldn't find your account — please sign in again.", StatusCodes.Status403Forbidden);
        }

        try
        {
            var result = await _chat.DeleteSessionsAsync(
                UserContext.UserId, cardiMemberId, request.SessionIds, ct);
            return Success(result);
        }
        catch (KeyNotFoundException ex)
        {
            return Error(ex.Message, StatusCodes.Status404NotFound);
        }
    }

    /// <summary>Reopens a completed conversation as the caregiver's active one and returns its
    /// turns for the chat window to continue from. Whatever was active is ended in the same
    /// stroke — one live conversation per member. 404 under the same existence-hiding rule as
    /// reading a session.</summary>
    [HttpPost("members/{cardiMemberId:guid}/sessions/{sessionId:guid}/continue")]
    [ProducesResponseType(typeof(ApiResponse<MemberChatHistoryResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<MemberChatHistoryResponse>>> ContinueSession(
        Guid cardiMemberId, Guid sessionId, CancellationToken ct)
    {
        if (!UserContext.IsAuthenticated || UserContext.UserId == Guid.Empty)
        {
            return Error("We couldn't find your account — please sign in again.", StatusCodes.Status403Forbidden);
        }

        try
        {
            var result = await _chat.ContinueSessionAsync(UserContext.UserId, cardiMemberId, sessionId, ct);
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
