namespace CardiTrack.Mobile.Services;

/// <summary>
/// Reloads a screen the moment the app returns to the foreground, so a caregiver picking their
/// phone back up reads what the workers have processed since they last looked rather than what
/// was on screen when they put it down. Pull-to-refresh stays — it is no longer the only way.
/// </summary>
internal static class ResumeRefresh
{
    /// <summary>
    /// A load that has just run covers the resume as well. Android raises <c>OnAppearing</c>
    /// again on its way back to the foreground and iOS does not, so without this the two
    /// platforms would differ by one duplicate request per resume.
    /// </summary>
    public static readonly TimeSpan MinimumGap = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Runs <paramref name="refresh"/> on every foreground transition while
    /// <paramref name="page"/> is the screen being looked at. Call it from the page constructor;
    /// the page's own load method keeps deciding what "refresh" means and whether the data on
    /// screen is fresh enough to skip it.
    /// </summary>
    public static void RefreshWhenAppResumes(this Page page, Func<Task> refresh)
    {
        var notifier = ServiceHelper.GetRequiredService<IAppResumeNotifier>();

        async void OnResumed(object? sender, EventArgs e)
        {
            if (!ScreenRefresh.IsOnScreen(page))
                return;

            try
            {
                await refresh();
            }
            catch (Exception ex)
            {
                // async void, reached from an event: nothing above this can catch it, and a
                // refresh nobody asked for must not be the thing that kills the app.
                ScreenRefresh.LogFailure(ex, page, "after the app resumed");
            }
        }

        // Subscription follows the visual tree rather than the page's lifetime. Pages pushed
        // over a tab are transient, and a singleton event holding on to every one ever opened
        // would keep those pages — and everything they captured — alive for the whole session.
        page.Loaded += (_, _) =>
        {
            notifier.Resumed -= OnResumed;
            notifier.Resumed += OnResumed;
        };
        page.Unloaded += (_, _) => notifier.Resumed -= OnResumed;
    }
}
