using System.Diagnostics;
using CardiTrack.Application.Interfaces.Clients;
using CardiTrack.Application.Interfaces.Security;
using CardiTrack.Application.Services.Notifications;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Diagnostics;
using FirebaseAdmin.Messaging;
using Microsoft.Extensions.Logging;

namespace CardiTrack.Infrastructure.ExternalClients.Push;

/// <summary>
/// FCM HTTP v1 send — one integration, both platforms, relaying to APNs for iOS
/// (notification_engine.md §2, §5). Resolved via ADC (the Cloud Run default compute SA holds
/// <c>roles/firebasecloudmessaging.admin</c>, granted in #108/PR176) — no service-account key
/// file, no <see cref="FirebaseAdmin.AppOptions"/> credential override.
///
/// Privacy invariant (§7.1): payload titles/bodies built here are always PHI-free teasers from
/// <see cref="PushTeaser"/> (kind of alert + "open the app") — never Alert.Message or a name.
/// No delivery content may reach a log, span attribute, or exception message this class produces.
/// </summary>
public class FcmNotificationChannel : INotificationChannel
{
    private const int MaxAttempts = 2;

    /// <summary>
    /// The Android drawable the small icon resolves to. Matches the MAUI image resource of the
    /// same name in the mobile app (<c>Resources/Images/icon_notification.svg</c>) and the
    /// <c>default_notification_icon</c> meta-data in its manifest — all three name one asset, and
    /// renaming it means renaming it in all three.
    /// </summary>
    private const string SmallIconName = "icon_notification";

    /// <summary>Tints the small icon and the notification's accent — CardiTrack's primary blue.</summary>
    private const string SmallIconColor = "#135497";

    private readonly FirebaseMessaging _messaging;
    private readonly IEncryptionService _encryption;
    private readonly IAckTokenService _ackTokens;
    private readonly ILogger<FcmNotificationChannel> _logger;
    private readonly TimeProvider _timeProvider;

    public FcmNotificationChannel(
        FirebaseMessaging messaging,
        IEncryptionService encryption,
        IAckTokenService ackTokens,
        ILogger<FcmNotificationChannel> logger,
        TimeProvider? timeProvider = null)
    {
        _messaging = messaging;
        _encryption = encryption;
        _ackTokens = ackTokens;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<SendResult> SendAsync(
        NotificationDelivery delivery, PushDeviceToken token, CancellationToken ct = default)
    {
        using var activity = PushTelemetry.Source.StartActivity("fcm.send", ActivityKind.Client);
        activity?.SetTag(PushTelemetry.DeliveryIdTag, delivery.Id.ToString());
        activity?.SetTag(PushTelemetry.CategoryTag, delivery.Category.ToString());

        var message = BuildMessage(delivery, token);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var providerMessageId = await SendWithRetryAsync(message, ct);

            PushTelemetry.Sent.Add(1, new KeyValuePair<string, object?>(PushTelemetry.CategoryTag, delivery.Category.ToString()));
            _logger.LogInformation(
                "FCM send succeeded for delivery {DeliveryId}: {ElapsedMs} ms, provider message {ProviderMessageId}.",
                delivery.Id, stopwatch.ElapsedMilliseconds, providerMessageId);

            return new SendResult.Sent(providerMessageId);
        }
        catch (FirebaseMessagingException ex)
        {
            var reason = ex.MessagingErrorCode?.ToString() ?? ex.ErrorCode.ToString();
            activity?.SetStatus(ActivityStatusCode.Error, reason);
            activity?.SetTag(PushTelemetry.ErrorTypeTag, reason);

            // FCM's own signal that this token will never work again — APNs 410 surfaces through
            // the same code. Nothing else is permanent: an invalid-argument or config error is a
            // deploy problem, not evidence the token is dead, so it stays Retryable rather than
            // wrongly disabling a live token.
            if (ex.MessagingErrorCode == MessagingErrorCode.Unregistered)
            {
                _logger.LogWarning(
                    "FCM reported delivery {DeliveryId}'s token as unregistered — disabling it.",
                    delivery.Id);
                PushTelemetry.DeadLettered.Add(1);
                return new SendResult.Permanent(reason);
            }

            _logger.LogWarning(
                "FCM send failed for delivery {DeliveryId} after {ElapsedMs} ms: {Reason} (retryable).",
                delivery.Id, stopwatch.ElapsedMilliseconds, reason);
            PushTelemetry.Failed.Add(1, new KeyValuePair<string, object?>(PushTelemetry.ReasonTag, reason));
            return new SendResult.Retryable(reason);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var errorType = ex.GetType().Name;
            activity?.SetStatus(ActivityStatusCode.Error, errorType);
            activity?.SetTag(PushTelemetry.ErrorTypeTag, errorType);
            _logger.LogWarning(ex,
                "FCM send threw for delivery {DeliveryId} after {ElapsedMs} ms (retryable).",
                delivery.Id, stopwatch.ElapsedMilliseconds);
            PushTelemetry.Failed.Add(1, new KeyValuePair<string, object?>(PushTelemetry.ReasonTag, errorType));
            return new SendResult.Retryable(errorType);
        }
        finally
        {
            PushTelemetry.FcmSendDuration.Record(stopwatch.Elapsed.TotalSeconds);
        }
    }

    /// <summary>
    /// Retries a transport-level failure (the RPC itself never completed) up to
    /// <see cref="MaxAttempts"/> times — distinct from the <c>Retryable</c> outcome the caller
    /// gets back for a completed-but-unsuccessful send, which is the Worker's retry/backoff to
    /// own (§6.2), not this client's.
    /// </summary>
    private async Task<string> SendWithRetryAsync(Message message, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await _messaging.SendAsync(message, ct);
            }
            catch (FirebaseMessagingException)
            {
                // The RPC completed and FCM had an opinion — that's the caller's Retryable/
                // Permanent classification to make, not a transport retry.
                throw;
            }
            catch (Exception) when (attempt < MaxAttempts)
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt), _timeProvider, ct);
            }
        }
    }

    /// <summary>Internal, not private, so <c>PayloadPrivacyTests</c> can assert the content-free invariant directly.</summary>
    internal Message BuildMessage(NotificationDelivery delivery, PushDeviceToken token)
    {
        // The same allowlist DeliveryPlanner evaluated at enqueue time (Safety always; Health
        // only when Red) — reusing the one function rather than re-deriving a second copy is what
        // keeps this and the enqueue-time decision from drifting apart (§7.2, Critical Alerts
        // abuse surface: the flag must never depend on where in the pipeline you ask).
        var allowsCritical = DeliveryPlanner.AllowsCritical(delivery.Category, delivery.Severity);

        // Design assumes Time Sensitive for anything that pages a caregiver (Safety + Health
        // red/orange). Critical Alerts is an upgrade the code is ready to flip on per-category
        // once the entitlement (#106) is granted — until then "critical" is capped at
        // "time-sensitive" even where the allowlist would permit it, since actually requesting
        // aps interruption-level=critical without the entitlement gets the whole payload
        // rejected by APNs (§4). Orange used to land as "active", which Focus modes swallow —
        // a silent health alert is the failure mode this widens past.
        var interruptionLevel = allowsCritical
            || (delivery.Category == DeliveryCategory.Health
                && delivery.Severity is AlertSeverity.Red or AlertSeverity.Orange)
            ? "time-sensitive"
            : "active";

        var expirationSeconds = new DateTimeOffset(delivery.ExpiresAt).ToUnixTimeSeconds();

        // Single-use, per-delivery tokens (§5, §7.2 C3/C5) — issued fresh on every send attempt
        // (including retries), scoped to this exact delivery + device pair and expiring with the
        // message itself, so a stale attempt's token can't outlive the alert it was for.
        var ackToken = _ackTokens.IssueAckToken(delivery.Id, token.Id, delivery.ExpiresAt);
        var fetchToken = _ackTokens.IssueFetchToken(delivery.Id, token.Id, delivery.ExpiresAt);

        return new Message
        {
            // The FCM SDK needs the raw registration token — decrypted here, at the one call site
            // that actually sends, rather than carried in plaintext any earlier in the pipeline.
            //
            // Token, not Fid, despite FirebaseAdmin 3.6.0 marking this property deprecated in
            // favour of it. They are not two names for one value: `fid` targets a Firebase
            // Installation ID, and what PushDeviceToken stores is a registration token from
            // Plugin.Firebase's GetTokenAsync (PushRegistrationCoordinator). Google's own guidance
            // is to move to `fid` *once clients register using Installation IDs* — ours do not, and
            // moving this field alone just puts the wrong identifier in the right slot. `token`
            // keeps working through the transition; migrating means changing what the app
            // registers first, then this line, together.
            //
            // The CS0618 suppression is the whole point of the comment above: silencing the
            // deprecation warning by switching field is what broke delivery. Lift it when
            // PushRegistrationCoordinator registers an Installation ID, not before.
#pragma warning disable CS0618 // Message.Token is deprecated in favour of Fid — see above.
            Token = _encryption.Decrypt(token.Token),
#pragma warning restore CS0618
            Data = new Dictionary<string, string>
            {
                ["deliveryId"] = delivery.Id.ToString(),
                ["category"] = delivery.Category.ToString(),
                ["sourceType"] = delivery.SourceType.ToString(),
                ["sourceId"] = delivery.SourceId.ToString(),
                // Derived from the source identity rather than fetched — content-free payloads
                // mean the app resolves full detail after opening anyway, so a DB round trip here
                // just to populate a deep link would cost every send a query for no benefit.
                ["deepLink"] = delivery.SourceType == DeliverySourceType.Alert
                    ? $"carditrack://alerts/{delivery.SourceId}"
                    : $"carditrack://notifications/{delivery.SourceId}",
                ["ackToken"] = ackToken,
                ["fetchToken"] = fetchToken
            },
            // Content-free of PHI (§7.1): no names, no metrics. Title/body still vary by what
            // kind of alert this is so a lock screen is worth opening — and always ask for the app.
            // The notification service extension (or Android's data-message handler) can fetch
            // richer copy later when ShowDetailsOnLockScreen is on; until then this is what shows.
            Notification = BuildTeaser(delivery),
            Android = new AndroidConfig
            {
                Priority = Priority.High,
                TimeToLive = delivery.ExpiresAt - DateTime.UtcNow,
                CollapseKey = delivery.CollapseKey,
                Notification = new AndroidNotification
                {
                    ChannelId = delivery.Category switch
                    {
                        DeliveryCategory.Safety => NotificationChannels.Safety,
                        DeliveryCategory.Health => NotificationChannels.Health,
                        _ => NotificationChannels.Nudges
                    },
                    // Named here as well as in the manifest default. Android's small icon has to be
                    // a white-on-transparent silhouette; with neither this nor the manifest
                    // meta-data set it falls back to the adaptive launcher icon, which the system
                    // renders as a featureless grey square — the notification arrived with nothing
                    // on it to say who was calling.
                    Icon = SmallIconName,
                    Color = SmallIconColor,
                    Sound = AndroidSound(delivery.Category),
                    DefaultVibrateTimings = delivery.Category == DeliveryCategory.Safety
                }
            },
            Apns = new ApnsConfig
            {
                Headers = new Dictionary<string, string>
                {
                    ["apns-priority"] = "10",
                    ["apns-push-type"] = "alert",
                    ["apns-expiration"] = expirationSeconds.ToString()
                }.Concat(delivery.CollapseKey is null
                        ? []
                        : new Dictionary<string, string> { ["apns-collapse-id"] = delivery.CollapseKey })
                    .ToDictionary(kv => kv.Key, kv => kv.Value),
                Aps = new Aps
                {
                    Sound = IosSound(delivery.Category),
                    MutableContent = true,
                    CustomData = new Dictionary<string, object> { ["interruption-level"] = interruptionLevel }
                }
            }
        };
    }

    private static FirebaseAdmin.Messaging.Notification BuildTeaser(NotificationDelivery delivery)
    {
        var (title, body) = PushTeaser.For(delivery.Category, delivery.Severity, delivery.AlertType);
        return new FirebaseAdmin.Messaging.Notification { Title = title, Body = body };
    }

    /// <summary>Safety/Health share the unlock chime; Nudges use the shorter ding.</summary>
    private static string AndroidSound(DeliveryCategory category) =>
        category is DeliveryCategory.Safety or DeliveryCategory.Health
            ? NotificationChannels.AlertSound
            : NotificationChannels.NudgeSound;

    private static string IosSound(DeliveryCategory category) =>
        category is DeliveryCategory.Safety or DeliveryCategory.Health
            ? NotificationChannels.AlertSoundFile
            : NotificationChannels.NudgeSoundFile;
}
