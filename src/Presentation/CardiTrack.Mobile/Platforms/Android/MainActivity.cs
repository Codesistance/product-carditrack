using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using CardiTrack.Mobile.Services;
using Plugin.Firebase.CloudMessaging;

namespace CardiTrack.Mobile.Platforms.Android;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    // Required by Plugin.Firebase.CloudMessaging (see cloud_messaging.md "Android specifics") —
    // without these, a notification tapped while the app was killed never reaches
    // IFirebaseCloudMessaging.NotificationTapped.
    //
    // Deliberately no SplashScreen.SetKeepOnScreenCondition here. Holding the system splash until
    // MAUI's first frame was tried and measured: it froze the splash for 3.5+ seconds of startup
    // (every captured frame byte-identical), replacing SplashPage's spinner with a static picture
    // and no sign of progress. Matching the mark's shape — see SplashPage.xaml — turned out to be
    // the part that made the handover invisible; suppressing the handover as well only cost the
    // caregiver the one thing on screen that said the app was still working.
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        FirebaseCloudMessagingImplementation.OnNewIntent(Intent);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        FirebaseCloudMessagingImplementation.OnNewIntent(intent);
    }

    /// <summary>
    /// Registers the back-press callback that returns a caregiver to the page a tab jump dropped.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It goes on <see cref="AndroidX.Activity.ComponentActivity.OnBackPressedDispatcher"/> rather
    /// than in <c>Shell.OnBackButtonPressed</c>, which was tried and never fired: this app targets
    /// an SDK where predictive back is on, so the press arrives as an <c>OnBackInvokedCallback</c>
    /// and the legacy path MAUI raises that override from is dead code on this device.
    /// </para>
    /// <para>
    /// Registered on resume rather than in <c>OnCreate</c>, and only once. The dispatcher serves
    /// its callbacks most-recently-added first, and MAUI adds its own while the window and Shell
    /// handler come up — after <c>OnCreate</c> has returned. Adding ours there would put it
    /// underneath MAUI's, which finishes the activity at a tab root and would never let ours run.
    /// </para>
    /// </remarks>
    protected override void OnResume()
    {
        base.OnResume();

        if (_backCallback is not null)
            return;

        _backCallback = new ReturnToOriginCallback(this);
        OnBackPressedDispatcher.AddCallback(this, _backCallback);
    }

    private ReturnToOriginCallback? _backCallback;

    private sealed class ReturnToOriginCallback(MainActivity activity)
        : AndroidX.Activity.OnBackPressedCallback(enabled: true)
    {
        public override void HandleOnBackPressed()
        {
            if (TabNavigation.TryReturnToOrigin())
                return;

            // Not a press we have an answer for. Step out of the way and hand it back to the
            // dispatcher so MAUI's own callback — pop a page, or finish the activity — sees it.
            // Disabling first is what stops this from calling itself.
            Enabled = false;
            try
            {
                activity.OnBackPressedDispatcher.OnBackPressed();
            }
            finally
            {
                Enabled = true;
            }
        }
    }
}
