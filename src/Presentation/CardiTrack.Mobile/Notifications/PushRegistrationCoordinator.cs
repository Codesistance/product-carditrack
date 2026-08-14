// The whole file, not just members within it: Plugin.Firebase.CloudMessaging is only referenced
// (see CardiTrack.Mobile.csproj) on android/ios, so every type this file touches is simply
// absent on the Windows target — there is nothing to make conditional inside the class, the
// class itself doesn't exist there.
#if ANDROID || IOS
using CardiTrack.Application.Services.Notifications;
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

    private const string SafetyChannelId = NotificationChannels.Safety;
    private const string HealthChannelId = NotificationChannels.Health;
    private const string NudgesChannelId = NotificationChannels.Nudges;

    /// <summary>
    /// The white silhouette Android paints in the status bar. Matches
    /// <c>Resources/Images/icon_notification.svg</c>, the manifest's
    /// <c>default_notification_icon</c>, and <c>FcmNotificationChannel.SmallIconName</c> on the
    /// server — one asset, named in four places, and renaming it means renaming it in all of them.
    /// </summary>
    private const string NotificationSmallIconName = "icon_notification";

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
                osAuthorizationStatus: await AuthorizationStatusAsync(),
                safetyChannelEnabled: SafetyChannelEnabled(),
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

    /// <summary>
    /// What the OS actually says about this app's permission to notify — the value
    /// <c>PUSH_UNREACHABLE</c> is armed from, server-side.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This used to report <see cref="OsAuthorizationStatus.Granted"/> unconditionally, on the
    /// reasoning that Plugin.Firebase raises <c>Error</c> on a denial and reaching this line meant
    /// it had not. That is not the same claim: the plugin's own request is not the OS grant, a
    /// caregiver can revoke notifications in Settings long after the app last asked, and every
    /// registration afterwards would still have said "granted". The server believed every Android
    /// device was reachable, so the one gap the safety net exists to catch — nobody is listening —
    /// was the gap it could not see.
    /// </para>
    /// <para>
    /// Android is asked directly, including the runtime request the app never explicitly made:
    /// POST_NOTIFICATIONS has been a prompted permission since API 33 and the manifest entry alone
    /// grants nothing. iOS keeps the plugin's flow, which owns the UNUserNotificationCenter prompt
    /// there — but no longer keeps the assumption: a token in hand is reported as granted, and the
    /// denial path returns without registering at all (a denied iOS device has no token to send).
    /// </para>
    /// </remarks>
    private static async Task<OsAuthorizationStatus> AuthorizationStatusAsync()
    {
#if ANDROID
        var status = await Permissions.CheckStatusAsync<Permissions.PostNotifications>();
        if (status != PermissionStatus.Granted)
            status = await Permissions.RequestAsync<Permissions.PostNotifications>();

        return status switch
        {
            PermissionStatus.Granted => OsAuthorizationStatus.Granted,
            PermissionStatus.Unknown => OsAuthorizationStatus.NotDetermined,
            _ => OsAuthorizationStatus.Denied,
        };
#else
        // iOS: the prompt is the plugin's, and GetTokenAsync returning a token above is APNs
        // having issued one, which it does not do for an app the user has refused.
        return await Task.FromResult(OsAuthorizationStatus.Granted);
#endif
    }

    /// <summary>
    /// Whether a Safety push would actually surface on this device — the channel exists, is not
    /// muted to <c>IMPORTANCE_NONE</c>, and notifications are not switched off app-wide.
    /// </summary>
    /// <remarks>
    /// Also previously hardcoded true. Android lets a user silence one channel and leave the rest,
    /// which is exactly the state worth knowing about here: the app is registered, the token is
    /// live, pushes are accepted — and the safety-class alerts, the only ones that matter at 3am,
    /// land silently in a drawer nobody opens.
    /// </remarks>
    private static bool SafetyChannelEnabled()
    {
#if ANDROID
        var context = global::Android.App.Application.Context;
        var manager = (global::Android.App.NotificationManager?)context.GetSystemService(
            global::Android.Content.Context.NotificationService);
        if (manager is null)
            return false;

        if (!manager.AreNotificationsEnabled())
            return false;

        var channel = manager.GetNotificationChannel(SafetyChannelId);
        // A channel this app creates at construction, so absent means the OS has not caught up
        // rather than that the user silenced anything — not a claim of "disabled".
        return channel is null || channel.Importance != global::Android.App.NotificationImportance.None;
#else
        // iOS has no per-channel equivalent; authorization is the whole answer there.
        return true;
#endif
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
    /// notification can be posted to it. Safety at IMPORTANCE_HIGH wakes the device from Doze
    /// and plays the bundled alert sound with vibration; Health at DEFAULT still sounds and
    /// vibrates but does not heads-up. Nudges at IMPORTANCE_LOW stay near-silent.
    /// </summary>
    private static void RegisterAndroidChannels()
    {
        // The API 26 guard that used to stand here is gone with the floor: notification channels
        // arrived in 26 and SupportedOSPlatformVersion is now 31, so there is no build this can
        // run on without them.
        var context = global::Android.App.Application.Context;
        var manager = (global::Android.App.NotificationManager?)context.GetSystemService(global::Android.Content.Context.NotificationService);
        if (manager is null)
            return;

        // v1 channels froze without an explicit sound/vibration. The OS will not let those
        // attributes change in place, so v2 replaces them and the silent originals go.
        manager.DeleteNotificationChannel(NotificationChannels.SafetyLegacy);
        manager.DeleteNotificationChannel(NotificationChannels.HealthLegacy);

        var sound = AlertSoundUri(context);
        var audio = new global::Android.Media.AudioAttributes.Builder()
            .SetUsage(global::Android.Media.AudioUsageKind.Notification)
            .SetContentType(global::Android.Media.AudioContentType.Sonification)
            .Build();

        var safety = new global::Android.App.NotificationChannel(
            SafetyChannelId, "Safety alerts", global::Android.App.NotificationImportance.High)
        {
            Description = "Monitoring is down, or nobody is listening."
        };
        safety.EnableVibration(true);
        safety.SetVibrationPattern([0L, 500L, 200L, 500L]);
        safety.SetSound(sound, audio);
        manager.CreateNotificationChannel(safety);

        var health = new global::Android.App.NotificationChannel(
            HealthChannelId, "Health alerts", global::Android.App.NotificationImportance.Default)
        {
            Description = "A red or orange anomaly in a wearer's data."
        };
        health.EnableVibration(true);
        health.SetVibrationPattern([0L, 280L, 140L, 280L]);
        health.SetSound(sound, audio);
        manager.CreateNotificationChannel(health);

        manager.CreateNotificationChannel(new global::Android.App.NotificationChannel(
            NudgesChannelId, "Reminders", global::Android.App.NotificationImportance.Low)
        {
            Description = "Data-completeness nudges."
        });

        FirebaseCloudMessagingImplementation.ChannelId = SafetyChannelId;

        // The manifest's default_notification_icon covers the notifications the FCM SDK renders;
        // this covers the ones Plugin.Firebase builds itself, which read this static instead and
        // otherwise fall back to the launcher icon (see the manifest comment for why that arrives
        // as a blank square).
        //
        // Resolved by name rather than through the generated Resource class: the drawable is
        // produced from a MauiImage at build time, and looking it up by identifier keeps this from
        // depending on when in the build that generation lands. A miss is logged rather than
        // swallowed — a silently-zero icon ref is the exact failure this change exists to fix.
        var iconRef = context.Resources?.GetIdentifier(
            NotificationSmallIconName, "drawable", context.PackageName) ?? 0;
        if (iconRef == 0)
            Log.Warning("Notification small icon '{Name}' did not resolve to a drawable.", NotificationSmallIconName);
        else
            FirebaseCloudMessagingImplementation.SmallIconRef = iconRef;
    }

    private static global::Android.Net.Uri AlertSoundUri(global::Android.Content.Context context)
    {
        var resId = context.Resources?.GetIdentifier(
            NotificationChannels.AlertSound, "raw", context.PackageName) ?? 0;
        if (resId == 0)
        {
            Log.Warning("Alert sound '{Name}' did not resolve to a raw resource — using the system notification sound.",
                NotificationChannels.AlertSound);
            return global::Android.Media.RingtoneManager.GetDefaultUri(global::Android.Media.RingtoneType.Notification);
        }

        return global::Android.Net.Uri.Parse($"android.resource://{context.PackageName}/{resId}");
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
