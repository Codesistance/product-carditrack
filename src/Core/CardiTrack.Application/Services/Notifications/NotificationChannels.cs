namespace CardiTrack.Application.Services.Notifications;

/// <summary>
/// OS notification-channel ids and the bundled alert sound the FCM payload names.
/// Android channel ids are versioned (<c>.v2</c>) because the OS freezes a channel's sound
/// and vibration the first time it is created — bumping the id is how a later release can
/// actually change those (notification_engine.md §4). iOS has no channels; it plays
/// <see cref="AlertSoundFile"/> from the app bundle. Keep the iOS
/// <c>Platforms/iOS/Resources/</c> copy and the Android <c>res/raw/</c> copy
/// byte-identical — <c>AlertSoundAssetTests</c> fails if they drift.
/// </summary>
public static class NotificationChannels
{
    public const string Safety = "carditrack.safety.v2";
    public const string Health = "carditrack.health.v2";
    public const string Nudges = "carditrack.nudges";

    /// <summary>Ids created by the first push-spine release. Deleted on upgrade so the OS
    /// settings screen does not keep a silent duplicate next to the v2 channel.</summary>
    public const string SafetyLegacy = "carditrack.safety";
    public const string HealthLegacy = "carditrack.health";

    /// <summary>Android <c>res/raw</c> name — no extension.</summary>
    public const string AlertSound = "carditrack_alert";

    /// <summary>iOS main-bundle filename, including extension. APNs <c>sound</c> takes this.</summary>
    public const string AlertSoundFile = "carditrack_alert.wav";
}
