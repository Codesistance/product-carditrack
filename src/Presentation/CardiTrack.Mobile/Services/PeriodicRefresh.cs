namespace CardiTrack.Mobile.Services;

/// <summary>
/// Reloads a screen on a timer while the caregiver is looking at it, so a dashboard left open goes
/// on updating itself instead of showing whatever happened to be true when it was opened.
/// </summary>
/// <remarks>
/// <para>
/// Until this existed every refresh in the app was edge-triggered — navigation, app resume, or a
/// deliberate pull. A caregiver watching the screen saw nothing change until they touched it, which
/// is the wrong way round for a monitoring product: the screen they are staring at is exactly the
/// one that should keep itself current.
/// </para>
/// <para>
/// A timer, because there is nothing to subscribe to. Push exists but is alert-only and
/// user-visible — there is no silent data channel that could tell an open page its data moved — so
/// polling is the only option the client has today. Interval choice belongs to the caller, but
/// nothing is gained below the server's own collection cadence.
/// </para>
/// </remarks>
internal static class PeriodicRefresh
{
    /// <summary>
    /// How often a live screen re-reads what the server has already computed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A read of stored, computed values — <c>GET</c> against the dashboard, member detail and
    /// alerts endpoints. It never asks the server to go and poll the wearable; that is the manual
    /// refresh button's job and the Worker's, and a tick that did it would earn the manual-sync
    /// cooldown's refusal and pop a dialog for something nobody asked for.
    /// </para>
    /// <para>
    /// Thirty seconds, where every screen used to sit between two and five minutes. Those windows
    /// were set against the Worker's ten-minute polling sync being the only way data arrived, on
    /// the reasoning that asking more often than the numbers could change spends battery to redraw
    /// the same screen. That is no longer how data arrives: webhook-triggered syncs land within
    /// seconds of the provider reporting, and the GCP aggregator and assessor jobs run every five
    /// minutes offset from each other (see docs/technical/data_sync_architecture.md), so a
    /// reading, an assessment or an alert can now appear at any moment. A five-minute screen could
    /// sit on a superseded number for most of that. Thirty seconds rather than ten because this is
    /// a screen a caregiver leaves open: the cost is paid per minute of watching, and ten seconds
    /// would triple it to shorten the worst case by twenty.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan LiveDataInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Runs <paramref name="refresh"/> every <paramref name="interval"/> while
    /// <paramref name="page"/> is the screen being looked at. Call it from the page constructor;
    /// the page's own load method keeps deciding what "refresh" means.
    /// </summary>
    /// <param name="refresh">
    /// Must be the quiet, read-only path. A tick that asked the server to go and poll the wearable
    /// would earn the manual-sync cooldown's refusal and pop a dialog for something nobody asked
    /// for.
    /// </param>
    public static void RefreshEvery(this Page page, TimeSpan interval, Func<Task> refresh)
    {
        IDispatcherTimer? timer = null;

        // Started and stopped with the visual tree, like the resume subscription and for the same
        // reason: pages are transient, and a running timer holding one keeps that page — and
        // everything it captured — alive for the rest of the session.
        page.Loaded += (_, _) =>
        {
            timer ??= CreateTimer(page, interval, refresh);
            timer.Start();
        };
        page.Unloaded += (_, _) => timer?.Stop();
    }

    private static IDispatcherTimer CreateTimer(Page page, TimeSpan interval, Func<Task> refresh)
    {
        var timer = page.Dispatcher.CreateTimer();
        timer.Interval = interval;
        timer.IsRepeating = true;

        timer.Tick += async (_, _) =>
        {
            // The timer runs while the page is loaded, which is not the same as being in front of
            // the caregiver: Shell keeps visited tabs loaded, and a modal covers the page without
            // unloading it.
            if (!ScreenRefresh.IsOnScreen(page))
                return;

            try
            {
                await refresh();
            }
            catch (Exception ex)
            {
                // async void, reached from a timer: nothing above this can catch it, and a refresh
                // nobody asked for must not be the thing that kills the app.
                ScreenRefresh.LogFailure(ex, page, "on its timer");
            }
        };

        return timer;
    }
}
