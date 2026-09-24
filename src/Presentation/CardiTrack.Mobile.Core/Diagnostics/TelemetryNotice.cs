using CardiTrack.Mobile.Core.Auth;

namespace CardiTrack.Mobile.Core.Diagnostics;

/// <summary>
/// The one-time notice that session telemetry is on by default, and what to do about it if the
/// caregiver would rather it were not. A notice, not a choice: its only answer is "Got it", and
/// the switch it points to lives in Settings → Privacy, where it can be changed any time.
/// </summary>
/// <remarks>
/// "Seen" is remembered per caregiver, keyed by the same one-way token as the health-data
/// disclosure hint (<see cref="HealthDataDisclosureScope"/>): a phone that changes hands — even
/// through a session that expired without the Settings sign-out running — shows it again to
/// the next person rather than letting one caregiver's acknowledgement stand for theirs.
/// </remarks>
public static class TelemetryNotice
{
    public const string Title = "About app diagnostics";

    public const string Message =
        "CardiTrack sends us information about how the app is running — the screens you open, "
        + "errors, crashes and how quickly it responds — so we can find and fix problems. It never "
        + "includes health data, your name or your email, though it may be possible to "
        + "match it to your account when we look into a problem.\n\n"
        + "If you'd rather it didn't, turn off Send session telemetry in Settings › Privacy. "
        + "You can change it any time.";

    public const string AcknowledgeText = "Got it";

    public const string OpenSettingsText = "Open Settings";

    /// <summary>
    /// The value to store once the caregiver has seen the notice, or null when there is no
    /// signed-in identity to tie it to — in which case nothing is remembered and it shows again.
    /// </summary>
    public static string? SeenValueFor(string? email) => HealthDataDisclosureScope.For(email);

    /// <summary>Whether <paramref name="stored"/> records that this caregiver has seen it.</summary>
    public static bool IsSeen(string? stored, string? email) =>
        SeenValueFor(email) is { } scope && string.Equals(stored, scope, StringComparison.Ordinal);
}
