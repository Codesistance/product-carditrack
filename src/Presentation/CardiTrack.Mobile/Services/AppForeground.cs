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
        var activity = Platform.CurrentActivity;
        if (activity is null)
            return;

        // Always retarget MainActivity. Starting CurrentActivity.Class used to relaunch
        // the callback activity (or a Custom Tab) when that was still "current", which
        // is exactly the browser "Go to Dashboard" must not walk back into.
        var intent = new Android.Content.Intent(activity, typeof(Platforms.Android.MainActivity));
        intent.AddFlags(
            Android.Content.ActivityFlags.ClearTop
            | Android.Content.ActivityFlags.SingleTop
            | Android.Content.ActivityFlags.ReorderToFront
            | Android.Content.ActivityFlags.NewTask);
        activity.StartActivity(intent);

        // CLEAR_TOP finishes a Custom Tab sitting above us in this task. A sibling Chrome
        // task is a different stack — MoveTaskToFront is what puts ours back on screen.
        if (activity is Platforms.Android.MainActivity
            && activity.GetSystemService(Android.Content.Context.ActivityService) is Android.App.ActivityManager manager)
        {
            manager.MoveTaskToFront(activity.TaskId, Android.App.MoveTaskFlags.None);
        }
#endif
    }
}
