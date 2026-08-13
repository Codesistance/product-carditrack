// The whole file, not just members within it: Plugin.Firebase.CloudMessaging is only referenced
// (see CardiTrack.Mobile.csproj) on android/ios, so every type this file touches is simply
// absent on the Windows target — there is nothing to make conditional inside the class, the
// class itself doesn't exist there.
#if ANDROID || IOS
using CardiTrack.Domain.Enums;
using CardiTrack.Mobile.Core.Http;
using CardiTrack.Mobile.Core.Notifications;
using CardiTrack.Mobile.Core.Onboarding;
using CardiTrack.Mobile.Services;
using Plugin.Firebase.CloudMessaging;
using Plugin.Firebase.CloudMessaging.EventArgs;
using Serilog;
// MAUI's own Microsoft.Maui.Devices.DevicePlatform collides with this one — aliased rather
// than fully-qualifying every use below.
using DevicePlatform = CardiTrack.Domain.Enums.DevicePlatform;

namespace CardiTrack.Mobile.Notifications;

/// <summary>
/// Orchestrates the platform side of push registration: requests the OS permission at the moment
/// of value (notification_engine.md §4 — after the first device connection succeeds, not on
/// launch), retrieves the FCM token, and registers it. Android/iOS only — there is no Windows
/// implementation to swap in, so this type simply isn't constructed there (see MauiProgram.cs's
/// <c>#if ANDROID || IOS</c> guard around its registration).
/// </summary>
public sealed class PushRegistrationCoordinator : IDisposable
{
    /// <summary>
    /// Ceiling on the stored value: PushDeviceToken.AppVersion is varchar(32) and NOT NULL (see
    /// PushDeviceTokenConfiguration), so an over-long version has to be clipped here rather than
    /// rejected — a device that can't register its push token is a device that can't be alerted.
    /// </summary>
    private const int MaxStoredAppVersionLength = 32;

    private const string SafetyChannelId = "carditrack.safety";
    private const string HealthChannelId = "carditrack.health";
    private const string NudgesChannelId = "carditrack.nudges";

    private readonly IFirebaseCloudMessaging _messaging;
    private readonly IPushDeviceRegistrationService _registration;
    private readonly ISecureKeyValueStore _keyValueStore;

    /// <summary>Raised when a tapped notification's deep link has been parsed — AppShell subscribes to navigate.</summary>
    public event EventHandler<NudgeDestination>? DestinationTapped;

    public PushRegistrationCoordinator(
        IFirebaseCloudMessaging messaging,
        IPushDeviceRegistrationService registration,
        ISecureKeyValueStore keyValueStore)
    {
        _messaging = messaging;
        _registration = registration;
        _keyValueStore = keyValueStore;

        _messaging.NotificationReceived += OnNotificationReceived;
        _messaging.NotificationTapped += OnNotificationTapped;
        _messaging.Error += OnError;

#if ANDROID
        RegisterAndroidChannels();
#endif
    }

    /// <summary>
    /// Requests the OS permission, then registers the resulting token. Errors are swallowed —
    /// per §4, a denied or failed registration just means <c>PUSH_UNREACHABLE</c> arms on the
    /// server side next time reachability is evaluated; it must never block the onboarding flow
    /// that called this.
    /// </summary>
    public async Task RequestPermissionAndRegisterAsync(CancellationToken ct = default)
    {
        try
        {
            await _messaging.CheckIfValidAsync();
            var token = await _messaging.GetTokenAsync();

            if (string.IsNullOrWhiteSpace(token))
                return;

            var deviceId = await GetOrCreateDeviceIdAsync();

            await _registration.RegisterAsync(
                deviceId,
                platform: DeterminePlatform(),
                appVersion: CurrentAppVersion(),
                token: token,
                // Plugin.Firebase's CheckIfValidAsync doesn't return a status — it raises Error
                // when the OS denies the request (OnError below). Reaching here without that
                // event having fired is the closest signal to "granted" this abstraction exposes.
                osAuthorizationStatus: OsAuthorizationStatus.Granted,
                safetyChannelEnabled: true,
                ct);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Push registration failed — will retry on next foreground.");
        }
    }

    /// <summary>
    /// The background handler's ack — posted the moment the push arrives, before any user
    /// interaction (§6.3). Fire-and-forget is deliberate: a slow or offline ack must never delay
    /// the OS from displaying the notification, and a missed ack just means the escalation ladder
    /// runs one step further before <c>MarkDelivered</c> catches up (or a foreground sync does).
    /// </summary>
    private void OnNotificationReceived(object? sender, FCMNotificationReceivedEventArgs e)
    {
        var data = e.Notification.Data;
        if (data is null
            || !data.TryGetValue("deliveryId", out var deliveryIdRaw)
            || !Guid.TryParse(deliveryIdRaw, out var deliveryId)
            || !data.TryGetValue("ackToken", out var ackToken))
        {
            return;
        }

        _ = AckDeliveredSafeAsync(deliveryId, ackToken);
    }

    private async Task AckDeliveredSafeAsync(Guid deliveryId, string ackToken)
    {
        try
        {
            await _registration.AckDeliveredAsync(deliveryId, ackToken);
        }
        catch (Exception ex)
        {
            // Not fatal — an unacked delivery just rides the escalation ladder one step further,
            // or gets picked up by the next foreground sync (§6.4). Logged so a systemic ack
            // failure (e.g. the API unreachable) is visible without being treated as a crash.
            Log.Warning(ex, "Delivered-ack failed for NotificationDelivery {DeliveryId}.", deliveryId);
        }
    }

    private void OnNotificationTapped(object? sender, FCMNotificationTappedEventArgs e)
    {
        var data = e.Notification.Data;
        var deepLink = data is not null && data.TryGetValue("deepLink", out var link) ? link : null;
        var destination = NudgeLinkParser.Parse(deepLink);
        if (destination.Kind != NudgeDestinationKind.Unknown)
            DestinationTapped?.Invoke(this, destination);
    }

    private static void OnError(object? sender, FCMErrorEventArgs e) =>
        Log.Warning("Push messaging error: {Message}", e.Message);

    /// <summary>
    /// Stable per install, distinct from the FCM token itself (which can rotate) — persisted in
    /// the same encrypted store as the auth token store, rather than a MAUI device-hardware id
    /// API (deprecated across platforms for privacy reasons and not guaranteed stable anyway).
    /// </summary>
    private async Task<string> GetOrCreateDeviceIdAsync()
    {
        const string key = "push_device_id";
        var existing = await _keyValueStore.GetAsync(key);
        if (!string.IsNullOrWhiteSpace(existing))
            return existing;

        var created = Guid.NewGuid().ToString("N");
        await _keyValueStore.SetAsync(key, created);
        return created;
    }

    /// <summary>
    /// The build registering the token, in the same "1.4.2+37" form the API's X-Client-Version
    /// header carries — so a token that stops delivering can be tied to the build that registered
    /// it, and to that build's traces.
    ///
    /// This previously sent a hardcoded "1.0" described as a DTO-shape version. Nothing server-side
    /// ever read it that way: DeviceTokenService only stores the value, so the column named
    /// AppVersion now actually holds one.
    ///
    /// Never returns empty — the column is NOT NULL, and no version string is worth losing a push
    /// registration over.
    /// </summary>
    private static string CurrentAppVersion()
    {
        var version = ClientHeaders.FormatVersion(AppInfo.Current.VersionString, AppInfo.Current.BuildString)
            ?? AppInfo.Current.VersionString;

        if (string.IsNullOrWhiteSpace(version))
            return "unknown";

        return version.Length <= MaxStoredAppVersionLength ? version : version[..MaxStoredAppVersionLength];
    }

    private static DevicePlatform DeterminePlatform() =>
#if IOS
        DevicePlatform.Ios;
#elif ANDROID
        DevicePlatform.Android;
#else
        throw new PlatformNotSupportedException("Push is not supported on this platform.");
#endif

#if ANDROID
    /// <summary>
    /// Registered once at app start — Android requires a channel to exist before any
    /// notification can be posted to it. Safety at IMPORTANCE_HIGH wakes the device from Doze;
    /// nudges at IMPORTANCE_LOW is the "near-silent by design" channel (§3), though nudges never
    /// push today (only the two safety-class rules do, which arrive as Safety, not Nudge).
    /// </summary>
    private static void RegisterAndroidChannels()
    {
        // Notification channels don't exist before API 26 — SupportedOSPlatformVersion for this
        // target is 23 (the Datadog SDK's floor), so this guard is load-bearing, not decorative.
        if (!OperatingSystem.IsAndroidVersionAtLeast(26))
            return;

        var context = global::Android.App.Application.Context;
        var manager = (global::Android.App.NotificationManager?)context.GetSystemService(global::Android.Content.Context.NotificationService);
        if (manager is null)
            return;

        manager.CreateNotificationChannel(new global::Android.App.NotificationChannel(
            SafetyChannelId, "Safety alerts", global::Android.App.NotificationImportance.High)
        {
            Description = "Monitoring is down, or nobody is listening."
        });

        manager.CreateNotificationChannel(new global::Android.App.NotificationChannel(
            HealthChannelId, "Health alerts", global::Android.App.NotificationImportance.Default)
        {
            Description = "A red or orange anomaly in a wearer's data."
        });

        manager.CreateNotificationChannel(new global::Android.App.NotificationChannel(
            NudgesChannelId, "Reminders", global::Android.App.NotificationImportance.Low)
        {
            Description = "Data-completeness nudges."
        });

        FirebaseCloudMessagingImplementation.ChannelId = SafetyChannelId;
    }
#endif

    public void Dispose()
    {
        _messaging.NotificationReceived -= OnNotificationReceived;
        _messaging.NotificationTapped -= OnNotificationTapped;
        _messaging.Error -= OnError;
    }
}
#endif
