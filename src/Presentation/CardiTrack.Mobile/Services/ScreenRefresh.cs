using Microsoft.Extensions.Logging;

namespace CardiTrack.Mobile.Services;

/// <summary>
/// What every unattended refresh has to agree on: whether the page is actually being looked at,
/// and what to do when a refresh nobody asked for fails.
/// </summary>
/// <remarks>
/// Shared by <see cref="ResumeRefresh"/> and <see cref="PeriodicRefresh"/> rather than written
/// twice. The on-screen test in particular carries reasoning about Shell's retained tabs and the
/// modal stack that would rot the moment two copies of it existed.
/// </remarks>
internal static class ScreenRefresh
{
    /// <summary>
    /// True only for the page actually being looked at. Shell keeps the pages of visited tabs
    /// alive, so without this one trigger would refresh every tab the caregiver has ever opened.
    /// </summary>
    public static bool IsOnScreen(Page page) =>
        page.Window is not null
        && Shell.Current is { } shell
        && shell.CurrentPage == page
        // A modal — a popup, or the connect-device wizard — owns the screen while it is up, and
        // the wizard's own completion path reloads whatever is underneath. Read from the page's
        // navigation, which is where PopupService and WizardLauncher push them, and what
        // App and AppPopupPage already read the modal stack from.
        && page.Navigation.ModalStack.Count == 0;

    /// <param name="trigger">
    /// What asked for the refresh, so a log line says which unattended path failed rather than
    /// leaving two of them indistinguishable.
    /// </param>
    public static void LogFailure(Exception ex, Page page, string trigger)
    {
        try
        {
            ServiceHelper.GetRequiredService<ILoggerFactory>()
                .CreateLogger(typeof(ScreenRefresh))
                .LogWarning(ex, "Refreshing {Page} {Trigger} failed", page.GetType().Name, trigger);
        }
        catch
        {
            // Nothing left to report it with.
        }
    }
}
