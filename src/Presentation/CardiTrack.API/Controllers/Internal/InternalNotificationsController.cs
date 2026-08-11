using CardiTrack.API.Extensions;
using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Interfaces.Security;
using CardiTrack.Application.Services.Notifications;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CardiTrack.API.Controllers.Internal;

/// <summary>
/// Service-to-service only — the AI pipeline's <c>SeverityRouter</c> calls this once it has
/// written an <see cref="CardiTrack.Domain.Entities.Alert"/> row (notification_engine.md §12).
/// Deliberately not under <c>api/v1/notifications</c> and not on the default Auth0 scheme: this
/// is the pipeline's transport into the same rules engine every other producer uses, not a copy
/// of it (§2) — recipient resolution, quiet hours, dedup and escalation all happen inside
/// <see cref="IDispatchService.EnqueueForAlertAsync"/>, identically to a Worker-raised alert.
/// </summary>
[ApiController]
[Authorize(AuthenticationSchemes = GoogleOidcExtensions.SchemeName)]
[Route("api/v1/internal/notifications")]
[Produces("application/json")]
public class InternalNotificationsController : ControllerBase
{
    private readonly IDispatchService _dispatch;
    private readonly INotificationContentService _content;
    private readonly IAckTokenService _ackTokens;
    private readonly ILogger<InternalNotificationsController> _logger;

    public InternalNotificationsController(
        IDispatchService dispatch,
        INotificationContentService content,
        IAckTokenService ackTokens,
        ILogger<InternalNotificationsController> logger)
    {
        _dispatch = dispatch;
        _content = content;
        _ackTokens = ackTokens;
        _logger = logger;
    }

    /// <summary>
    /// Defence in depth beyond the OIDC scheme: this route should also sit behind Cloud Run IAM
    /// (<c>roles/run.invoker</c> granted only to the pipeline service account, per Terraform) so
    /// the platform rejects an unauthorized caller before application code ever runs.
    /// </summary>
    [HttpPost("enqueue")]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<object>>> Enqueue(
        [FromBody] EnqueueDeliveryRequest request, CancellationToken ct)
    {
        var results = await _dispatch.EnqueueForAlertAsync(request.AlertId, ct);

        if (results.Count == 0)
        {
            _logger.LogWarning(
                "Internal enqueue for Alert {AlertId} produced no deliveries — missing alert or no recipients.",
                request.AlertId);
        }

        return Ok(new ApiResponse<object>
        {
            Success = true,
            Message = $"Enqueued {results.Count} deliver{(results.Count == 1 ? "y" : "ies")}.",
            Timestamp = DateTime.UtcNow
        });
    }

    /// <summary>
    /// What the iOS notification service extension (and Android's data-message handler) fetch
    /// to rewrite a content-free payload before display (§5, §7.1). <c>[AllowAnonymous]</c> and
    /// scoped by <c>fetchToken</c>, not the controller's OIDC scheme — this is called by a phone,
    /// not the pipeline. §7.2 C5: never the user's access token, so the NSE (a separate process
    /// on every push) never holds a credential wider than this one delivery.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("{deliveryId:guid}/content")]
    [ProducesResponseType(typeof(ApiResponse<NotificationContentResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<NotificationContentResponse>>> GetContent(
        Guid deliveryId, [FromQuery] string fetchToken, CancellationToken ct)
    {
        // The token embeds and authenticates its own pushDeviceTokenId (see AckTokenService) —
        // there is nothing further to cross-check; a validated token is the whole authorization.
        if (!_ackTokens.ValidateFetchToken(fetchToken, deliveryId, out _))
            return NotFound(new ErrorResponse { Success = false, Message = "Not found.", Timestamp = DateTime.UtcNow });

        var content = await _content.GetContentAsync(deliveryId, ct);
        if (content is null)
            return NotFound(new ErrorResponse { Success = false, Message = "Not found.", Timestamp = DateTime.UtcNow });

        return Ok(new ApiResponse<NotificationContentResponse>
        {
            Success = true,
            Message = "Here you go!",
            Data = content,
            Timestamp = DateTime.UtcNow
        });
    }
}
