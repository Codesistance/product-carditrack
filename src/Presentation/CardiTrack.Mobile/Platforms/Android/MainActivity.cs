using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
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
}
