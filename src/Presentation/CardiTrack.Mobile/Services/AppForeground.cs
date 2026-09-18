#if ANDROID
using Android.Content;
#endif

namespace CardiTrack.Mobile.Services;

/// <summary>
/// Brings the app's task to the foreground after a system-browser OAuth round-trip.
/// Chrome Custom Tabs (and a full Chrome tab opened by the bounce page) can stay in the
/// Android task after the deep link fires; without this, a later in-app navigation —
/// notably "Go to Dashboard" — walks back into that browser instead of the page it named.
/// </summary>
internal static class AppForeground
{
    public static void BringToFront()
    {
#if ANDROID
        try
        {
            // The launcher intent, not an explicit component intent. Both name MainActivity, but
            // only ACTION_MAIN + CATEGORY_LAUNCHER matches the *base intent* of the task the app
            // was started in, which is what makes Android resume that task — exactly as tapping
            // the app icon does. An explicit `new Intent(activity, typeof(MainActivity))` carries
            // no action or category, so a NEW_TASK launch of it is free to resolve somewhere
            // other than the app's own launcher task, and the browser task stays in front.
            var context = (Context?)Platform.CurrentActivity
                ?? global::Android.App.Application.Context;
            var packageName = context.PackageName;
            if (packageName is null)
                return;

            var intent = context.PackageManager?.GetLaunchIntentForPackage(packageName)
                ?? new Intent(context, typeof(Platforms.Android.MainActivity));

            // CLEAR_TOP | SINGLE_TOP finishes a Custom Tab sitting above us without recreating
            // MainActivity (it gets the intent through OnNewIntent). NEW_TASK is what a
            // SingleTask MainActivity needs to take the foreground when Chrome opened a sibling
            // task, and is required at all when the context is not an activity. Do not combine
            // CLEAR_TOP with REORDER_TO_FRONT — Android documents those as mutually exclusive,
            // and the pair used to leave the bounce on the wrong task. Do not call
            // ActivityManager.MoveTaskToFront either; that needs REORDER_TASKS.
            intent.AddFlags(
                ActivityFlags.ClearTop
                | ActivityFlags.SingleTop
                | ActivityFlags.NewTask);
            context.StartActivity(intent);
        }
        catch (Exception)
        {
            // Best-effort. Foregrounding must not take down device-connect or skip
            // the DashboardExit signal the modal launcher waits on.
        }
#endif
    }
}
