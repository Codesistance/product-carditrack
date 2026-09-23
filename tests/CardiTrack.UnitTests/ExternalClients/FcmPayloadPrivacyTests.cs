using CardiTrack.Application.Interfaces.Security;
using CardiTrack.Application.Services.Notifications;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.ExternalClients.Push;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
// The FCM request body is Newtonsoft-serialised by FirebaseAdmin itself — the event_time test
// below asserts what actually goes on the wire, so it uses the same serialiser the SDK does.
using Newtonsoft.Json;
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
    private FcmNotificationChannel CreateSut(TimeProvider? timeProvider = null) =>
        new(null!, _encryption, _ackTokens, NullLogger<FcmNotificationChannel>.Instance, timeProvider);

    private static NotificationDelivery Delivery(
        DeliveryCategory category = DeliveryCategory.Nudge,
        AlertSeverity? severity = null,
        DeliverySourceType sourceType = DeliverySourceType.Notification,
        string? collapseKey = null,
        AlertType? alertType = null) => new()
        {
            Id = Guid.NewGuid(),
            SourceType = sourceType,
            SourceId = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            Category = category,
            Severity = severity,
            AlertType = alertType,
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
    [InlineData(DeliveryCategory.Safety, "CardiTrack", "Urgent — open CardiTrack now")]
    [InlineData(DeliveryCategory.Health, "Health alert", "Open CardiTrack to check on this.")]
    [InlineData(DeliveryCategory.Nudge, "CardiTrack", "Something needs your attention — open CardiTrack")]
    public void Notification_IsAlwaysThePhiFreeTeaser_NeverDeliverySpecificText(
        DeliveryCategory category, string expectedTitle, string expectedBody)
    {
        var message = CreateSut().BuildMessage(Delivery(category), Token());

        Assert.Equal(expectedTitle, message.Notification.Title);
        Assert.Equal(expectedBody, message.Notification.Body);
    }

    [Theory]
    [InlineData(AlertType.HeartRate, "Heart rate alert")]
    [InlineData(AlertType.Sleep, "Sleep alert")]
    [InlineData(AlertType.Inactivity, "Activity alert")]
    [InlineData(AlertType.PatternBreak, "Pattern alert")]
    [InlineData(AlertType.Trend, "Trend alert")]
    public void HealthNotification_TitleVariesByAlertType_WithoutMetricsOrNames(
        AlertType alertType, string expectedTitle)
    {
        var message = CreateSut().BuildMessage(
            Delivery(DeliveryCategory.Health, AlertSeverity.Orange, alertType: alertType), Token());

        Assert.Equal(expectedTitle, message.Notification.Title);
        Assert.Equal("Open CardiTrack to check on this.", message.Notification.Body);
        Assert.DoesNotContain("bpm", message.Notification.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Margaret", message.Notification.Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HealthNotification_RedSeverity_UsesUrgentOpenBody()
    {
        var message = CreateSut().BuildMessage(
            Delivery(DeliveryCategory.Health, AlertSeverity.Red, alertType: AlertType.HeartRate), Token());

        Assert.Equal("Heart rate alert", message.Notification.Title);
        Assert.Equal("Urgent — open CardiTrack to check.", message.Notification.Body);
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

    // ── event_time: the header date Android renders (#498) ────────────────────

    [Fact]
    public void AndroidNotification_StampsTheEventTime_WithTheDeliverysEnqueueTime()
    {
        var delivery = Delivery(DeliveryCategory.Health, AlertSeverity.Orange, alertType: AlertType.PatternBreak);
        delivery.CreatedDate = new DateTime(2026, 9, 1, 18, 10, 0, DateTimeKind.Utc);

        var android = CreateSut().BuildMessage(delivery, Token()).Android.Notification;

        Assert.Equal(delivery.CreatedDate, android.EventTimestamp);
    }

    [Fact]
    public void AndroidNotification_NeverSerialisesTheYearOneDefault_IntoEventTime()
    {
        // The regression itself, asserted on the wire rather than on the property: FirebaseAdmin
        // declares EventTimestamp as a non-nullable DateTime and emits `event_time` whether or
        // not it was set, so "we didn't set it" shipped 0001-01-01 to every Android handset and
        // the push arrived headed 02/01/1. The shape of the bug is invisible from the C# side —
        // only the serialised body shows it — so this test reads the body.
        var delivery = Delivery(DeliveryCategory.Health, AlertSeverity.Red);
        delivery.CreatedDate = new DateTime(2026, 9, 1, 18, 10, 0, DateTimeKind.Utc);

        var json = JsonConvert.SerializeObject(CreateSut().BuildMessage(delivery, Token()).Android.Notification);

        Assert.Contains("\"event_time\":\"2026-09-01T18:10:00", json);
        Assert.DoesNotContain("0001-01-01", json);
    }

    [Fact]
    public void AndroidNotification_FallsBackToSendTime_WhenTheRowHasNoCreationTime()
    {
        // Wrong by minutes beats wrong by two thousand years: whatever leaves CreatedDate unset,
        // the one field that cannot be omitted must not go out as year 1.
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 23, 7, 0, 0, TimeSpan.Zero));
        var delivery = Delivery();
        delivery.CreatedDate = default;

        var android = CreateSut(clock).BuildMessage(delivery, Token()).Android.Notification;

        Assert.Equal(new DateTime(2026, 9, 23, 7, 0, 0, DateTimeKind.Utc), android.EventTimestamp);
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
    [InlineData(DeliveryCategory.Health, AlertSeverity.Orange, "time-sensitive")]
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
    public void ApnsAlert_AlsoSetsContentAvailable_SoIosCanWarmTheCache_WithoutChangingPushType()
    {
        var message = CreateSut().BuildMessage(Delivery(), Token());

        Assert.Equal("alert", message.Apns.Headers!["apns-push-type"]);
        Assert.True(message.Apns.Aps.ContentAvailable);
    }

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
