using CardiTrack.Domain.Enums;

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
    /// <summary>
    /// Health alerts (red/orange vitals). v4 = IMPORTANCE_HIGH so the OS actually plays sound and
    /// heads-up; v3 was DEFAULT and on many devices arrived silently in the shade.
    /// </summary>
    public const string Health = "carditrack.health.v4";
    public const string Nudges = "carditrack.nudges.v2";

    /// <summary>Ids from earlier releases. Deleted on upgrade so the OS settings screen
    /// does not keep a silent or vibrating duplicate next to the current channel.</summary>
    public const string SafetyLegacy = "carditrack.safety";
    public const string HealthLegacy = "carditrack.health";
    public const string HealthLegacyV2 = "carditrack.health.v2";
    public const string HealthLegacyV3 = "carditrack.health.v3";
    public const string NudgesLegacy = "carditrack.nudges";

    /// <summary>Android <c>res/raw</c> name for Safety/Health — no extension.</summary>
    public const string AlertSound = "carditrack_alert";

    /// <summary>iOS main-bundle filename for Safety/Health. APNs <c>sound</c> takes this.</summary>
    public const string AlertSoundFile = "carditrack_alert.wav";

    /// <summary>Android <c>res/raw</c> name for Nudges — no extension.</summary>
    public const string NudgeSound = "carditrack_nudge";

    /// <summary>iOS main-bundle filename for Nudges. APNs <c>sound</c> takes this.</summary>
    public const string NudgeSoundFile = "carditrack_nudge.wav";

    /// <summary>
    /// The one category→channel mapping, used by the server when it stamps
    /// <c>android.notification.channel_id</c> on the FCM payload and by the app when it posts
    /// the foreground copy of the same push — so the two can't route the same delivery to
    /// different channels.
    /// </summary>
    public static string ForCategory(DeliveryCategory category) => category switch
    {
        DeliveryCategory.Safety => Safety,
        DeliveryCategory.Health => Health,
        _ => Nudges
    };

    /// <summary>
    /// Android <c>res/raw</c> sound name for a category — Safety/Health share the unlock chime,
    /// Nudges use the shorter ding.
    /// </summary>
    /// <remarks>
    /// Sits beside <see cref="ForCategory"/> rather than inside the FCM client because the
    /// dev-only test-push endpoint reports the resolved sound back to the caller, and a second
    /// copy of this switch is exactly the kind that drifts silently: a wrong answer there would
    /// send someone hunting a device-side sound bug that does not exist. The value only takes
    /// effect on a channel's *first* creation — see the versioning note above.
    /// </remarks>
    public static string AndroidSoundFor(DeliveryCategory category) =>
        category is DeliveryCategory.Safety or DeliveryCategory.Health ? AlertSound : NudgeSound;

    /// <summary>iOS main-bundle sound filename for a category, for the APNs <c>aps.sound</c> field.</summary>
    public static string IosSoundFor(DeliveryCategory category) =>
        category is DeliveryCategory.Safety or DeliveryCategory.Health ? AlertSoundFile : NudgeSoundFile;
}
