using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Security;
using CardiTrack.Application.Services.Notifications;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CardiTrack.API.Controllers.Dev;

/// <summary>
/// Fires one real push on demand so it can be observed on a device or emulator
/// (notification_engine.md §13). Exists because the only other trigger is
/// <c>PushCanaryWorker</c>, which is cron-driven and has an empty fleet everywhere — reproducing
/// a push problem otherwise means waiting for a real alert.
/// </summary>
/// <remarks>
/// Routed only when <see cref="DevPushControllerProvider"/> says so: a configured
/// <c>Dev:PushTokenKey</c> and a non-prod environment, both required. Anonymous by necessity — the
/// point is to send without a signed-in caller — so the HMAC token in <c>X-Dev-Push-Token</c> is
/// the whole of the authorization, and it is scoped to one user and one payload shape.
/// <para>
/// Sends through <see cref="IDispatchService.EnqueueAsync"/> rather than reaching for
/// <c>INotificationChannel</c> directly, for the same reason <c>PushCanaryWorker</c> does: a test
/// push with its own shortcut send path can look healthy while the real path is broken. What
/// arrives here is byte-identical to a production alert.
/// </para>
/// <para>
/// The awaited-in-request-scope shape matters — this hosts no background work, so it does not
/// touch the CLAUDE.md rule reserving scheduled jobs to <c>CardiTrack.Worker</c>.
/// </para>
/// </remarks>
[ApiController]
[AllowAnonymous]
[Route("api/v1/dev/push")]
[Produces("application/json")]
public class DevPushController : ControllerBase
{
    /// <summary>Header carrying the HMAC token. Not <c>Authorization</c>: this is not a bearer credential and should not be picked up by anything that harvests one.</summary>
    public const string TokenHeader = "X-Dev-Push-Token";

    private const string DedupPrefix = "devpush";
    private const int FingerprintPrefixLength = 6;

    private readonly IDispatchService _dispatch;
    private readonly IDevPushTokenService _tokens;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<DevPushController> _logger;
    private readonly TimeProvider _timeProvider;

    public DevPushController(
        IDispatchService dispatch,
        IDevPushTokenService tokens,
        IUnitOfWork unitOfWork,
        ILogger<DevPushController> logger,
        TimeProvider? timeProvider = null)
    {
        _dispatch = dispatch;
        _tokens = tokens;
        _unitOfWork = unitOfWork;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    [HttpPost]
    [ProducesResponseType(typeof(ApiResponse<DevPushResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<ApiResponse<DevPushResponse>>> Send(
        [FromBody] DevPushRequest request, CancellationToken ct)
    {
        var presented = Request.Headers[TokenHeader].ToString();

        // One bare 401 for every failure mode. Distinguishing "expired" from "wrong user" from
        // "bad MAC" would tell a caller without the key which part of the tuple to vary next.
        if (!_tokens.Validate(presented, request.UserId, request.Category, request.Severity))
        {
            _logger.LogWarning(
                "Dev push rejected for user {UserId}: token missing, expired, or not valid for this request.",
                request.UserId);
            return Unauthorized(new ErrorResponse
            {
                Success = false,
                Message = "Invalid or expired dev push token.",
                Timestamp = _timeProvider.GetUtcNow().UtcDateTime
            });
        }

        if (request.Category == DeliveryCategory.Health && request.Severity is null)
        {
            return BadRequest(new ErrorResponse
            {
                Success = false,
                Message = "Health deliveries need a severity — only Red and Orange produce a push.",
                Timestamp = _timeProvider.GetUtcNow().UtcDateTime
            });
        }

        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;

        // Second resolution, unlike PushCanaryWorker's minute-resolution key. Two test pushes a
        // few seconds apart are the normal way this endpoint gets used, and a minute-granular key
        // would send the second one into DispatchService's dedup branch — returning the first
        // row, unsent, which is indistinguishable from the push being broken.
        var dedupKey = $"{DedupPrefix}:{request.UserId:N}:{utcNow:yyyyMMddHHmmssfff}";

        var delivery = await _dispatch.EnqueueAsync(new EnqueueRequest(
            SourceType: DeliverySourceType.Notification,
            SourceId: Guid.Empty,
            UserId: request.UserId,
            CardiMemberId: null,
            Category: request.Category,
            Severity: request.Severity,
            DedupKey: dedupKey,
            // No collapse key on purpose: a repeated test push must never replace the previous
            // one on the device, since seeing both arrive is half the point.
            CollapseKey: null), ct);

        _logger.LogWarning(
            "Dev push sent: delivery {DeliveryId}, user {UserId}, category {Category}, severity {Severity}.",
            delivery.Id, request.UserId, request.Category, request.Severity);

        var response = await BuildResponseAsync(delivery, dedupKey, request);
        return Ok(new ApiResponse<DevPushResponse>
        {
            Success = true,
            Message = response.Hint ?? "Push dispatched.",
            Data = response,
            Timestamp = utcNow
        });
    }

    private async Task<DevPushResponse> BuildResponseAsync(
        NotificationDelivery delivery, string dedupKey, DevPushRequest request)
    {
        // A multi-device fan-out clones the row per device, suffixing the dedup key (see
        // DispatchService.Clone), so the returned row is only the first device's. Re-reading by
        // prefix picks up the rest.
        var clonePrefix = dedupKey + ":";
        var rows = (await _unitOfWork.NotificationDeliveries
                .FindAsync(d => d.DedupKey == dedupKey || d.DedupKey.StartsWith(clonePrefix)))
            .ToList();

        if (rows.Count == 0)
            rows.Add(delivery);

        var devices = new List<DevPushDeviceResult>();
        foreach (var row in rows.Where(r => r.PushDeviceTokenId is not null))
        {
            var token = await _unitOfWork.PushDeviceTokens.GetByIdAsync(row.PushDeviceTokenId!.Value);
            if (token is null)
                continue;

            devices.Add(new DevPushDeviceResult
            {
                DeviceId = token.DeviceId,
                Platform = token.Platform,
                AppVersion = token.AppVersion,
                Fingerprint = Truncate(token.TokenFingerprint),
                OsAuthorizationStatus = token.OsAuthorizationStatus,
                SafetyChannelEnabled = token.SafetyChannelEnabled,
                LastAckDate = token.LastAckDate,
                State = row.State,
                ProviderMessageId = row.ProviderMessageId,
                LastError = row.LastError
            });
        }

        return new DevPushResponse
        {
            DeliveryId = delivery.Id,
            Channel = delivery.Channel,
            ScheduledFor = delivery.ScheduledFor,
            State = delivery.State,
            ExpiresAt = delivery.ExpiresAt,
            AndroidChannelId = NotificationChannels.ForCategory(request.Category),
            AndroidSound = NotificationChannels.AndroidSoundFor(request.Category),
            IosSound = NotificationChannels.IosSoundFor(request.Category),
            Devices = devices,
            Hint = Explain(delivery, devices.Count, request)
        };
    }

    /// <summary>
    /// The three ways this endpoint returns 200 having sent nothing. Each is silent in the data —
    /// a Pending row with no error looks the same in all three — so each gets said out loud.
    /// </summary>
    private static string? Explain(NotificationDelivery delivery, int deviceCount, DevPushRequest request)
    {
        if (delivery.Channel == DeliveryChannel.InApp)
        {
            return request.Category == DeliveryCategory.Nudge
                ? "Nudge deliveries never push — DeliveryPlanner routes every Nudge row to in-app. " +
                  "Use Safety, or Health with Red or Orange severity, to produce a notification."
                : $"DeliveryPlanner routed this to in-app, not push: Health only pushes at Red or " +
                  $"Orange severity, and this was {request.Severity?.ToString() ?? "unset"}.";
        }

        if (delivery.ScheduledFor is not null)
        {
            return $"Quiet hours deferred this until {delivery.ScheduledFor:u}. Nothing has been sent " +
                   "yet — the Worker's dispatch loop picks it up when it comes due. Safety and Red " +
                   "Health override quiet hours if you need it now.";
        }

        if (deviceCount == 0)
        {
            return "No live device token for this user, so nothing was sent. Sign in on the device to " +
                   "register one, and check the token is not disabled, its OS authorization is Granted " +
                   "or Provisional, and — for Safety — that the OS-level Safety channel is on.";
        }

        return null;
    }

    private static string Truncate(string fingerprint) =>
        fingerprint.Length <= FingerprintPrefixLength ? fingerprint : fingerprint[..FingerprintPrefixLength];
}
