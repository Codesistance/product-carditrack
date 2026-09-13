using Serilog;
#if ANDROID || IOS
using Datadog.Maui;
using Datadog.Maui.Configuration;
#endif

namespace CardiTrack.Mobile.Services;

/// <summary>
/// Whether the caregiver has agreed to send diagnostics (crash-adjacent logs and network
/// traces) to Datadog. Opt-in: off until they turn it on in Settings, and off again for
/// the next caregiver who signs in on the same phone.
/// </summary>
/// <remarks>
/// The off state is <see cref="TrackingConsent.NotGranted"/>, not Pending. Pending would
/// still collect and hold events on the device in the hope of a later yes — which is
/// collection without consent, exactly what an opt-in toggle is supposed to prevent. The
/// cost is that diagnostics from before the toggle was turned on are never recoverable,
/// which is the right trade for a health app.
/// </remarks>
public static class DiagnosticsConsent
{
    /// <summary>Preference key. Absent means not granted — the toggle ships off.</summary>
    public const string GrantedKey = "DiagnosticsConsentGranted";

    /// <summary>
    /// Reads the stored choice, defaulting to no. Guarded because the first read happens inside
    /// <c>MauiProgram.CreateMauiApp</c> — before the app is built — and a platform preference
    /// store that is not ready there would otherwise take the whole app down over a setting.
    /// Unreadable falls to no, which is the safe direction: it under-collects, never over-.
    /// </summary>
    public static bool IsGranted
    {
        get
        {
            try
            {
                return Preferences.Default.Get(GrantedKey, false);
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
    /// Forgets the choice on sign-out and stops collection immediately. The next caregiver
    /// on this phone is asked afresh rather than inheriting a yes they never gave.
    /// </summary>
    public static void Clear()
    {
        Preferences.Default.Remove(GrantedKey);
        Apply(false);
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
