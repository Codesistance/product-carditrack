using CardiTrack.Application.Interfaces.Security;
using CardiTrack.Application.Services.Notifications;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.ExternalClients.Push;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace CardiTrack.UnitTests.ExternalClients;

/// <summary>
/// notification_engine.md §7.1: "the user sees rich content, APNs never does" — the FCM request
/// body itself must never carry a delivery's real title, body, or any other PHI-adjacent field.
/// These pin the payload to its content-free shape and the critical-flag allowlist that gates it,
/// so a future edit to <c>FcmNotificationChannel.BuildMessage</c> that widens either one fails loud.
/// </summary>
public class FcmPayloadPrivacyTests
{
    private const string DecryptedToken = "raw-fcm-registration-token";
    private const string AckToken = "issued-ack-token";
    private const string FetchToken = "issued-fetch-token";

    private readonly IEncryptionService _encryption = Substitute.For<IEncryptionService>();
    private readonly IAckTokenService _ackTokens = Substitute.For<IAckTokenService>();

    public FcmPayloadPrivacyTests()
    {
        _encryption.Decrypt(Arg.Any<string>()).Returns(DecryptedToken);
        _ackTokens.IssueAckToken(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<DateTime>()).Returns(AckToken);
        _ackTokens.IssueFetchToken(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<DateTime>()).Returns(FetchToken);
    }

    // BuildMessage never touches the FirebaseMessaging client — passing null keeps this test
    // independent of the FirebaseAdmin SDK entirely (it isn't mockable, and doesn't need to be).
    private FcmNotificationChannel CreateSut() =>
        new(null!, _encryption, _ackTokens, NullLogger<FcmNotificationChannel>.Instance);

    private static NotificationDelivery Delivery(
        DeliveryCategory category = DeliveryCategory.Nudge,
        AlertSeverity? severity = null,
        DeliverySourceType sourceType = DeliverySourceType.Notification,
        string? collapseKey = null) => new()
        {
            Id = Guid.NewGuid(),
            SourceType = sourceType,
            SourceId = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            Category = category,
            Severity = severity,
            CollapseKey = collapseKey,
            ExpiresAt = DateTime.UtcNow.AddMinutes(30)
        };

    private static PushDeviceToken Token() => new()
    {
        Id = Guid.NewGuid(),
        UserId = Guid.NewGuid(),
        DeviceId = "device-1",
        Platform = DevicePlatform.Ios,
        Token = "encrypted-ciphertext"
    };

    // ── Content-free by default ───────────────────────────────────────────────

    [Theory]
    [InlineData(DeliveryCategory.Safety, "Urgent — open CardiTrack now")]
    [InlineData(DeliveryCategory.Health, "Something needs your attention — open CardiTrack")]
    [InlineData(DeliveryCategory.Nudge, "Something needs your attention — open CardiTrack")]
    public void Notification_IsAlwaysTheGenericContentFreeTeaser_NeverDeliverySpecificText(
        DeliveryCategory category, string expectedBody)
    {
        var message = CreateSut().BuildMessage(Delivery(category), Token());

        Assert.Equal("CardiTrack", message.Notification.Title);
        Assert.Equal(expectedBody, message.Notification.Body);
    }

    /// <summary>
    /// The teaser has to be worth opening as well as content-free. A body that names no app and
    /// asks for nothing ("Tap to view") passed every privacy assertion above and still told a
    /// caregiver nothing on a lock screen — so the shape is pinned, not just the exact strings.
    /// </summary>
    [Theory]
    [InlineData(DeliveryCategory.Safety)]
    [InlineData(DeliveryCategory.Health)]
    [InlineData(DeliveryCategory.Nudge)]
    public void Notification_NamesTheAppAndAsksForIt(DeliveryCategory category)
    {
        var body = CreateSut().BuildMessage(Delivery(category), Token()).Notification.Body;

        Assert.Contains("CardiTrack", body);
        Assert.Contains("open", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AndroidNotification_CarriesTheWhiteSilhouetteSmallIcon()
    {
        var android = CreateSut().BuildMessage(Delivery(), Token()).Android.Notification;

        // Without an explicit small icon Android falls back to the adaptive launcher icon and
        // alpha-masks it to a featureless square — see icon_notification.svg.
        Assert.Equal("icon_notification", android.Icon);
        Assert.False(string.IsNullOrWhiteSpace(android.Color));
    }

    [Theory]
    [InlineData(DeliveryCategory.Safety, NotificationChannels.Safety)]
    [InlineData(DeliveryCategory.Health, NotificationChannels.Health)]
    [InlineData(DeliveryCategory.Nudge, NotificationChannels.Nudges)]
    public void AndroidNotification_TargetsTheVersionedChannel(DeliveryCategory category, string expectedChannel)
    {
        var android = CreateSut().BuildMessage(Delivery(category), Token()).Android.Notification;

        Assert.Equal(expectedChannel, android.ChannelId);
    }

    [Theory]
    [InlineData(DeliveryCategory.Safety, NotificationChannels.AlertSoundFile, NotificationChannels.AlertSound, true)]
    [InlineData(DeliveryCategory.Health, NotificationChannels.AlertSoundFile, NotificationChannels.AlertSound, false)]
    [InlineData(DeliveryCategory.Nudge, NotificationChannels.NudgeSoundFile, NotificationChannels.NudgeSound, false)]
    public void Payload_PlaysTheCategorySound_AndOnlySafetyForcesVibration(
        DeliveryCategory category, string iosSound, string androidSound, bool vibrate)
    {
        var message = CreateSut().BuildMessage(Delivery(category), Token());

        Assert.Equal(iosSound, message.Apns.Aps.Sound);
        Assert.Equal(androidSound, message.Android.Notification.Sound);
        Assert.Equal(vibrate, message.Android.Notification.DefaultVibrateTimings);
    }

    [Fact]
    public void DataPayload_ContainsOnlyTheKnownSafeFieldSet_NothingElse()
    {
        var message = CreateSut().BuildMessage(Delivery(), Token());

        Assert.Equal(
            new HashSet<string> { "deliveryId", "category", "sourceType", "sourceId", "deepLink", "ackToken", "fetchToken" },
            message.Data.Keys.ToHashSet());
    }

    [Fact]
    public void TokenField_CarriesTheDecryptedRegistrationToken_NeverTheStoredCiphertext()
    {
        var deviceToken = Token();

        var message = CreateSut().BuildMessage(Delivery(), deviceToken);

#pragma warning disable CS0618 // Message.Token is deprecated in favour of Fid — see FcmNotificationChannel.BuildMessage.
        Assert.Equal(DecryptedToken, message.Token);
        Assert.NotEqual(deviceToken.Token, message.Token);
#pragma warning restore CS0618
    }

    [Fact]
    public void RegistrationToken_TargetsTheTokenField_NotFid()
    {
        // Fid is a Firebase Installation ID, a different identifier from the registration token
        // the app registers — see the comment on BuildMessage. Putting the token in `fid` sends
        // every push to a target FCM cannot resolve, and does so silently at the payload layer.
        var message = CreateSut().BuildMessage(Delivery(), Token());

        Assert.Null(message.Fid);
    }

    [Theory]
    [InlineData(DeliverySourceType.Alert, "carditrack://alerts/")]
    [InlineData(DeliverySourceType.Notification, "carditrack://notifications/")]
    public void DeepLink_IsDerivedFromSourceIdentity_NotFetchedContent(DeliverySourceType sourceType, string expectedPrefix)
    {
        var delivery = Delivery(sourceType: sourceType);

        var message = CreateSut().BuildMessage(delivery, Token());

        Assert.Equal($"{expectedPrefix}{delivery.SourceId}", message.Data["deepLink"]);
    }

    // ── Critical-alert allowlist: never settable from delivery/caller input ────

    [Theory]
    [InlineData(DeliveryCategory.Safety, null, "time-sensitive")]
    [InlineData(DeliveryCategory.Health, AlertSeverity.Red, "time-sensitive")]
    [InlineData(DeliveryCategory.Health, AlertSeverity.Orange, "active")]
    [InlineData(DeliveryCategory.Health, AlertSeverity.Yellow, "active")]
    [InlineData(DeliveryCategory.Health, null, "active")]
    [InlineData(DeliveryCategory.Nudge, null, "active")]
    public void InterruptionLevel_FollowsTheServerSideAllowlist_RegardlessOfSeverityAlone(
        DeliveryCategory category, AlertSeverity? severity, string expectedLevel)
    {
        var message = CreateSut().BuildMessage(Delivery(category, severity), Token());

        Assert.Equal(expectedLevel, message.Apns.Aps.CustomData!["interruption-level"]);
    }

    [Fact]
    public void InterruptionLevel_NeverActuallyRequestsAppleCriticalAlerts()
    {
        // §4: requesting interruption-level=critical without the Critical Alerts entitlement
        // (#106, not yet granted) gets the whole APNs payload rejected — so even a Safety/Red
        // delivery, which is the highest tier this codebase plans for, must cap at time-sensitive.
        var message = CreateSut().BuildMessage(Delivery(DeliveryCategory.Safety, AlertSeverity.Red), Token());

        Assert.NotEqual("critical", message.Apns.Aps.CustomData!["interruption-level"]);
    }

    // ── Tokens: fresh per send, never a caller-supplied or reused value ────────

    [Fact]
    public void AckAndFetchTokens_AreIssuedFreshForThisDeliveryAndDevice_NotDerivedFromCallerInput()
    {
        var delivery = Delivery();
        var token = Token();

        var message = CreateSut().BuildMessage(delivery, token);

        _ackTokens.Received(1).IssueAckToken(delivery.Id, token.Id, delivery.ExpiresAt);
        _ackTokens.Received(1).IssueFetchToken(delivery.Id, token.Id, delivery.ExpiresAt);
        Assert.Equal(AckToken, message.Data["ackToken"]);
        Assert.Equal(FetchToken, message.Data["fetchToken"]);
    }

    // ── Collapse key: only forwarded when present, never fabricated ────────────

    [Fact]
    public void ApnsCollapseId_IsOmitted_WhenDeliveryHasNoCollapseKey()
    {
        var message = CreateSut().BuildMessage(Delivery(collapseKey: null), Token());

        Assert.False(message.Apns.Headers!.ContainsKey("apns-collapse-id"));
    }

    [Fact]
    public void ApnsCollapseId_MirrorsTheDeliverysCollapseKey_WhenPresent()
    {
        var message = CreateSut().BuildMessage(Delivery(collapseKey: "worker:device-silence:abc:2026-08-11"), Token());

        Assert.Equal("worker:device-silence:abc:2026-08-11", message.Apns.Headers!["apns-collapse-id"]);
    }
}
