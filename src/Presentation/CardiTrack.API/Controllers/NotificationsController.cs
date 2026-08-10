using CardiTrack.API.Infrastructure.UserContext;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CardiTrack.API.Controllers;

/// <summary>
/// The in-app notification inbox — data-completeness gaps, and the three things a caregiver can do
/// about one: fix it, put it off, or turn it off.
/// </summary>
/// <remarks>
/// Health alerts live in <see cref="AlertsController"/>. These are about the account's
/// completeness rather than the monitored person's body, and they carry no acknowledgment
/// lifecycle — a gap resolves itself when the user closes it.
/// </remarks>
[Authorize]
[Route("api/v1/notifications")]
public class NotificationsController : BaseApiController
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;

    private readonly INotificationService _notifications;

    public NotificationsController(
        IUserContext userContext,
        ILogger<NotificationsController> logger,
        INotificationService notifications)
        : base(userContext, logger)
    {
        _notifications = notifications;
    }

    /// <summary>The caller's inbox, priority-ranked.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<NotificationListResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ApiResponse<NotificationListResponse>>> GetInbox(
        [FromQuery] string? state,
        [FromQuery] string? category,
        [FromQuery] Guid? cardiMemberId,
        [FromQuery] bool? owned,
        [FromQuery] int? limit,
        [FromQuery] int? offset,
        CancellationToken ct)
    {
        if (!TryGetUserId(out var userId, out var denied))
            return denied!;

        // Unrecognised filters are rejected rather than ignored: silently returning everything for
        // a typo'd filter is how a client ships a bug that looks like working software.
        if (!TryParseEnum<NotificationState>(state, out var parsedState))
            return Error($"'{state}' isn't a notification state we recognise.");

        if (!TryParseEnum<NotificationCategory>(category, out var parsedCategory))
            return Error($"'{category}' isn't a notification category we recognise.");

        var result = await _notifications.GetInboxAsync(
            userId, parsedState, parsedCategory, cardiMemberId, owned,
            Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit),
            Math.Max(offset ?? 0, 0),
            ct);

        return Success(result);
    }

    /// <summary>Badge count, safety banners and the dashboard card slots — one call on launch.</summary>
    [HttpGet("summary")]
    [ProducesResponseType(typeof(ApiResponse<NotificationSummaryResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ApiResponse<NotificationSummaryResponse>>> GetSummary(
        CancellationToken ct)
    {
        if (!TryGetUserId(out var userId, out var denied))
            return denied!;

        return Success(await _notifications.GetSummaryAsync(userId, ct));
    }

    /// <summary>Records that the caller has seen it. Idempotent — only the first sighting counts.</summary>
    [HttpPost("{notificationId:guid}/seen")]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<object>>> MarkSeen(Guid notificationId, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId, out var denied))
            return denied!;

        try
        {
            await _notifications.MarkSeenAsync(userId, notificationId, ct);
            return Success("Got it.");
        }
        catch (KeyNotFoundException ex)
        {
            return Error(ex.Message, StatusCodes.Status404NotFound);
        }
    }

    /// <summary>
    /// Puts it off. The requested duration is clamped to the rule's maximum rather than rejected,
    /// so a client asking for a month on a safety rule gets 72 hours and a success.
    /// </summary>
    [HttpPost("{notificationId:guid}/snooze")]
    [ProducesResponseType(typeof(ApiResponse<NotificationResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<NotificationResponse>>> Snooze(
        Guid notificationId, [FromBody] SnoozeNotificationRequest? request, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId, out var denied))
            return denied!;

        TimeSpan? duration = null;
        if (request?.Duration is { Length: > 0 } raw)
        {
            if (!TimeSpan.TryParse(raw, out var parsed) || parsed <= TimeSpan.Zero)
                return Error($"'{raw}' isn't a duration we can read. Try \"7.00:00:00\" for a week.");
            duration = parsed;
        }

        try
        {
            return Success(await _notifications.SnoozeAsync(userId, notificationId, duration, ct),
                "We'll remind you later.");
        }
        catch (KeyNotFoundException ex)
        {
            return Error(ex.Message, StatusCodes.Status404NotFound);
        }
    }

    /// <summary>
    /// Turns it off for good. Safety-class rules require <c>acknowledgedConsequence</c> — the caller
    /// must have shown the user what stops working, and the answer is recorded either way.
    /// </summary>
    [HttpPost("{notificationId:guid}/dismiss")]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<object>>> Dismiss(
        Guid notificationId, [FromBody] DismissNotificationRequest? request, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId, out var denied))
            return denied!;

        try
        {
            await _notifications.DismissAsync(
                userId, notificationId, request?.AcknowledgedConsequence ?? false, ct);
            return Success("We won't bring that up again.");
        }
        catch (KeyNotFoundException ex)
        {
            return Error(ex.Message, StatusCodes.Status404NotFound);
        }
        catch (InvalidOperationException ex)
        {
            return Error(ex.Message);
        }
    }

    /// <summary>Everything the caller has silenced — the settings screen's list.</summary>
    [HttpGet("mutes")]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<NotificationMuteResponse>>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<NotificationMuteResponse>>>> GetMutes(
        CancellationToken ct)
    {
        if (!TryGetUserId(out var userId, out var denied))
            return denied!;

        return Success(await _notifications.GetMutesAsync(userId, ct));
    }

    /// <summary>Un-silences one rule. Anything still outstanding reappears immediately.</summary>
    [HttpDelete("mutes/{muteId:guid}")]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<object>>> RemoveMute(Guid muteId, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId, out var denied))
            return denied!;

        try
        {
            await _notifications.RemoveMuteAsync(userId, muteId, ct);
            return Success("That one's back on.");
        }
        catch (KeyNotFoundException ex)
        {
            return Error(ex.Message, StatusCodes.Status404NotFound);
        }
    }

    /// <summary>"Show me everything again" — clears every mute the caller holds.</summary>
    [HttpPost("mutes/reset")]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<object>>> ResetMutes(CancellationToken ct)
    {
        if (!TryGetUserId(out var userId, out var denied))
            return denied!;

        var cleared = await _notifications.ResetMutesAsync(userId, ct);
        return Success($"Cleared {cleared} muted item{(cleared == 1 ? "" : "s")}.");
    }

    /// <summary>
    /// A valid token whose local user row does not exist yet — onboarding has not finished.
    /// Matches the 403 the other controllers return in the same situation.
    /// </summary>
    private bool TryGetUserId(out Guid userId, out ActionResult? denied)
    {
        userId = UserContext.UserId;

        if (!UserContext.IsAuthenticated || userId == Guid.Empty)
        {
            denied = Error("We couldn't find your account — please sign in again.",
                StatusCodes.Status403Forbidden);
            return false;
        }

        denied = null;
        return true;
    }

    private static bool TryParseEnum<TEnum>(string? raw, out TEnum? value) where TEnum : struct, Enum
    {
        value = null;
        if (string.IsNullOrWhiteSpace(raw))
            return true;

        if (!Enum.TryParse<TEnum>(raw, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
            return false;

        value = parsed;
        return true;
    }
}

public class SnoozeNotificationRequest
{
    /// <summary>A <see cref="TimeSpan"/> string such as <c>"7.00:00:00"</c>. Omit for the rule's default.</summary>
    public string? Duration { get; set; }
}

public class DismissNotificationRequest
{
    /// <summary>Required for Safety-class rules: confirms the user was shown what stops working.</summary>
    public bool AcknowledgedConsequence { get; set; }
}
