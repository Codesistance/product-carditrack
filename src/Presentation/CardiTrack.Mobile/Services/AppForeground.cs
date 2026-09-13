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
            var activity = Platform.CurrentActivity;
            if (activity is null)
                return;

            // Always retarget MainActivity. Starting CurrentActivity.Class used to relaunch
            // the callback activity (or a Custom Tab) when that was still "current", which
            // is exactly the browser "Go to Dashboard" must not walk back into.
            //
            // CLEAR_TOP | SINGLE_TOP finishes a Custom Tab sitting above us. NEW_TASK
            // is what a SingleTask MainActivity needs to take the foreground when
            // Chrome opened a sibling task. Do not combine CLEAR_TOP with
            // REORDER_TO_FRONT — Android documents those as mutually exclusive, and
            // the pair used to leave the bounce on the wrong task. Do not call
            // ActivityManager.MoveTaskToFront either; that needs REORDER_TASKS.
            var intent = new Android.Content.Intent(activity, typeof(Platforms.Android.MainActivity));
            intent.AddFlags(
                Android.Content.ActivityFlags.ClearTop
                | Android.Content.ActivityFlags.SingleTop
                | Android.Content.ActivityFlags.NewTask);
            activity.StartActivity(intent);
        }
        catch (Exception)
        {
            // Best-effort. Foregrounding must not take down device-connect or skip
            // the DashboardExit signal the modal launcher waits on.
        }
#endif
    }
}
