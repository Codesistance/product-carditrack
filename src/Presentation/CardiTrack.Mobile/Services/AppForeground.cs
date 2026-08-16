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

        // CLEAR_TOP finishes anything sitting above MainActivity in this task (the Custom
        // Tab). REORDER_TO_FRONT covers the case where Chrome opened a separate task.
        var intent = new Android.Content.Intent(activity, activity.Class);
        intent.AddFlags(
            Android.Content.ActivityFlags.ClearTop
            | Android.Content.ActivityFlags.SingleTop
            | Android.Content.ActivityFlags.ReorderToFront);
        activity.StartActivity(intent);
#endif
    }
}
