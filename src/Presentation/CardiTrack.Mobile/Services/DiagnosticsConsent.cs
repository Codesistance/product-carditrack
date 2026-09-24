using Serilog;
#if ANDROID || IOS
using Datadog.Maui;
using Datadog.Maui.Configuration;
#endif

namespace CardiTrack.Mobile.Services;

/// <summary>
/// Whether the app sends session telemetry (Datadog logs, network traces and RUM — views,
/// errors, crash reports) about how it is running. Off until a caregiver is signed in; from then
/// on by default, as disclosed in the Terms of Service and Privacy Policy they agreed to and the
/// one-time dashboard notice, and they can turn it off at any time in Settings → Privacy.
/// </summary>
/// <remarks>
/// <para>
/// Nothing is sent before sign-in. The UK statistical-purposes exception (PECR reg. 6 as amended
/// by the Data (Use and Access) Act 2025) replaces consent with clear information and a simple
/// way to object; before sign-in the caregiver has seen neither, and the switch is behind
/// sign-in. Crashes on those screens are still caught by the error-log relay
/// (<c>AppLogging</c>), which is fault detection — "strictly necessary" under the same Act —
/// and not governed by this switch (docs/compliance/dpia.md A9, R-A8).
/// </para>
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
        Apply();
    }

    /// <summary>
    /// Whether a caregiver is signed in on this run. Only ever set on the main thread. A property
    /// rather than a field: on targets without the SDK nothing reads it, and a field would warn.
    /// </summary>
    private static bool IsSignedIn { get; set; }

    /// <summary>
    /// A caregiver is signed in: from here their stored choice (on unless they turned it off)
    /// applies. Called by <see cref="PostLoginRouter"/>, which every kind of sign-in passes through.
    /// </summary>
    public static void SignedIn()
    {
        IsSignedIn = true;
        Apply();
    }

    /// <summary>
    /// The session ended without the Settings sign-out — it expired. Stops collection until the
    /// next sign-in; the caregiver's stored choice is left alone, since they did not sign out.
    /// </summary>
    public static void SignedOut()
    {
        IsSignedIn = false;
        Apply();
    }

    /// <summary>
    /// Forgets the choice on sign-out and stops collection until the next sign-in. A caregiver's
    /// "off" is theirs, not the phone's: the next person to sign in here gets the documented
    /// default and their own switch, not a setting somebody else chose.
    /// </summary>
    public static void Clear()
    {
        Preferences.Default.Remove(GrantedKey);
        SignedOut();
    }

    private static void Apply()
    {
#if ANDROID || IOS
        // Nothing was initialised when ApmEngine/ApmData are unset or malformed, and the
        // SDK's static entry points assume an initialised core. A monitoring call must
        // never take the app down over a setting the caregiver just changed.
        if (!MobileApm.IsConfigured)
            return;

        try
        {
            DdSdk.SetTrackingConsent(IsSignedIn && IsGranted ? TrackingConsent.Granted : TrackingConsent.NotGranted);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "DiagnosticsConsent: could not hand the new consent to the SDK.");
        }
#endif
    }
}
