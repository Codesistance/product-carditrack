using Serilog;
#if ANDROID || IOS
using Datadog.Maui;
using Datadog.Maui.Configuration;
#endif

namespace CardiTrack.Mobile.Services;

/// <summary>
/// Whether the app sends session telemetry (Datadog logs, network traces and RUM — views,
/// errors, crash reports) about how it is running. On by default and disclosed in the Terms of
/// Service and Privacy Policy the caregiver agrees to at sign-up; they can turn it off at any
/// time in Settings → Privacy, and it takes effect at once.
/// </summary>
/// <remarks>
/// <para>
/// The off state is <see cref="TrackingConsent.NotGranted"/>, not Pending. Pending would still
/// collect and hold events on the device in the hope of a later yes — so a caregiver who turned
/// it off would still be recorded, which is not what "off" means.
/// </para>
/// <para>
/// The stored value is only ever the caregiver's objection: absent means the default (on), and
/// sign-out removes it so one caregiver's choice is not inherited by — or imposed on — the next
/// person to sign in on the same phone. Nothing the SDK is handed carries health data, and
/// Session Replay is not enabled (<see cref="MobileApm"/>).
/// </para>
/// </remarks>
public static class DiagnosticsConsent
{
    /// <summary>Preference key. Absent means the default applies.</summary>
    public const string GrantedKey = "DiagnosticsConsentGranted";

    /// <summary>
    /// On unless the caregiver has turned it off. Disclosed in the Terms of Service and the
    /// Privacy Policy, with Settings → Privacy as the way out.
    /// </summary>
    public const bool DefaultGranted = true;

    /// <summary>
    /// Reads the stored choice, falling back to <see cref="DefaultGranted"/>. Guarded because the
    /// first read happens inside <c>MauiProgram.CreateMauiApp</c> — before the app is built — and
    /// a platform preference store that is not ready there would otherwise take the whole app
    /// down over a setting. Unreadable falls to no rather than the default: a store we cannot
    /// read might be holding a caregiver's "off", and honouring an objection we cannot see beats
    /// overriding one.
    /// </summary>
    public static bool IsGranted
    {
        get
        {
            try
            {
                return Preferences.Default.Get(GrantedKey, DefaultGranted);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "DiagnosticsConsent: could not read the stored choice — treating it as not granted.");
                return false;
            }
        }
    }

    /// <summary>
    /// Records the caregiver's choice and tells the SDK about it in the same breath, so a
    /// toggle takes effect without a restart.
    /// </summary>
    public static void Set(bool granted)
    {
        Preferences.Default.Set(GrantedKey, granted);
        Apply(granted);
    }

    /// <summary>
    /// Forgets the choice on sign-out and returns the SDK to the default. A caregiver's "off"
    /// is theirs, not the phone's: the next person to sign in here gets the documented default
    /// and their own switch, not a setting somebody else chose.
    /// </summary>
    public static void Clear()
    {
        Preferences.Default.Remove(GrantedKey);
        Apply(DefaultGranted);
    }

    private static void Apply(bool granted)
    {
#if ANDROID || IOS
        // Nothing was initialised when ApmEngine/ApmData are unset or malformed, and the
        // SDK's static entry points assume an initialised core. A monitoring call must
        // never take the app down over a setting the caregiver just changed.
        if (!MobileApm.IsConfigured)
            return;

        try
        {
            DdSdk.SetTrackingConsent(granted ? TrackingConsent.Granted : TrackingConsent.NotGranted);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "DiagnosticsConsent: could not hand the new consent to the SDK.");
        }
#endif
    }
}
