namespace CardiTrack.Application.Services.Notifications;

/// <summary>
/// OS notification-channel ids and the bundled sounds the FCM payload names.
/// Android channel ids are versioned because the OS freezes a channel's sound
/// and vibration the first time it is created — bumping the id is how a later release can
/// actually change those (notification_engine.md §4). iOS has no channels; it plays
/// <see cref="AlertSoundFile"/> / <see cref="NudgeSoundFile"/> from the app bundle.
/// Keep each iOS <c>Platforms/iOS/Resources/</c> copy byte-identical to the matching
/// Android <c>res/raw/</c> copy — <c>AlertSoundAssetTests</c> fails if they drift.
/// </summary>
public static class NotificationChannels
{
    public const string Safety = "carditrack.safety.v2";
    public const string Health = "carditrack.health.v3";
    public const string Nudges = "carditrack.nudges.v2";

    /// <summary>Ids from earlier releases. Deleted on upgrade so the OS settings screen
    /// does not keep a silent or vibrating duplicate next to the current channel.</summary>
    public const string SafetyLegacy = "carditrack.safety";
    public const string HealthLegacy = "carditrack.health";
    public const string HealthLegacyV2 = "carditrack.health.v2";
    public const string NudgesLegacy = "carditrack.nudges";

    /// <summary>Android <c>res/raw</c> name for Safety/Health — no extension.</summary>
    public const string AlertSound = "carditrack_alert";

    /// <summary>iOS main-bundle filename for Safety/Health. APNs <c>sound</c> takes this.</summary>
    public const string AlertSoundFile = "carditrack_alert.wav";

    /// <summary>Android <c>res/raw</c> name for Nudges — no extension.</summary>
    public const string NudgeSound = "carditrack_nudge";

    /// <summary>iOS main-bundle filename for Nudges. APNs <c>sound</c> takes this.</summary>
    public const string NudgeSoundFile = "carditrack_nudge.wav";
}
