using CardiTrack.Mobile.Core.Navigation;

namespace CardiTrack.Mobile.Services;

/// <summary>
/// Sending the caregiver to a tab without losing where they were. Pairs with
/// <see cref="BackNavigation"/>: that one decides what back does, this one makes sure back still
/// has something to do.
/// </summary>
internal static class TabNavigation
{
    /// <summary>
    /// The one place left behind by a tab jump. Static because the Shell is: there is one
    /// navigation history in this app, and threading an instance through every page's click
    /// handler would buy nothing.
    /// </summary>
    public static readonly NavigationOrigin Origin = new();

    /// <summary>
    /// Goes to an absolute tab route the way <see cref="Shell.GoToAsync(ShellNavigationState)"/>
    /// does, first recording the page it drops so back can return there.
    /// </summary>
    /// <param name="returnQuery">
    /// The query string the dropped page needs to rebuild itself — <c>memberId=…</c> for Member
    /// Detail. Shell's <see cref="Shell.CurrentState"/> reports the route without its parameters,
    /// so a page that is about someone has to name them itself or back returns to the route with
    /// no idea who it is for. Pages that take no parameters pass nothing.
    /// </param>
    /// <remarks>
    /// <para>
    /// Only a jump from a pushed page records anything. Shell's stack always holds the section
    /// root at index 0, so a count above one means there is a page here that the jump is about to
    /// throw away — the Member Detail a caregiver was reading when they tapped the bell. From a
    /// tab root the jump costs nothing, and back should keep Android's own meaning.
    /// </para>
    /// <para>
    /// Deliberately not used by <see cref="Controls.BottomNavBar"/>, which drops the stack the
    /// same way and records nothing. The difference is who decided to leave. A bell is a content
    /// affordance: it answers a question about the page you are on and takes the page away as a
    /// side effect nobody asked for, so back owes you the page. The nav bar is the caregiver
    /// saying "take me to that tab" — and from a pushed page its whole documented job is to take
    /// them down to the root, so returning them to the page they just used it to leave would undo
    /// the tap. Back from a tab root the caregiver chose exits, as Android expects.
    /// </para>
    /// </remarks>
    public static Task GoToTabAsync(this Shell shell, string absoluteRoute, string? returnQuery = null)
    {
        if (shell.Navigation.NavigationStack.Count > 1)
            Origin.Remember(Location(shell, returnQuery));
        else
            Origin.Clear();

        return shell.GoToAsync(absoluteRoute);
    }

    private static string? Location(Shell shell, string? returnQuery)
    {
        var location = shell.CurrentState?.Location?.ToString();
        return string.IsNullOrEmpty(location) || string.IsNullOrWhiteSpace(returnQuery)
            ? location
            : $"{location}?{returnQuery}";
    }

    /// <summary>
    /// Spends a recorded origin, if this is the moment for it: the caregiver is at a tab root they
    /// were sent to, and back would otherwise leave the app. Returns whether it took the press.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Shared by the platform back button and anything else that has to answer the same question.
    /// The navigation is started, not awaited — callers answer a synchronous "did you handle this".
    /// </para>
    /// <para>
    /// A popup owns the screen while it is up, and back belongs to it: modals sit in their own
    /// stack, so a page under one still reports a navigation stack of one and would otherwise look
    /// exactly like a tab root worth leaving. Taking that press would navigate out from under an
    /// open popup and leave the caregiver looking at a different page with it still on top. Same
    /// test <see cref="ScreenRefresh.IsOnScreen"/> makes, for the same reason.
    /// </para>
    /// </remarks>
    public static bool TryReturnToOrigin()
    {
        if (Shell.Current is not { } shell
            || shell.Navigation.NavigationStack.Count > 1
            || shell.Navigation.ModalStack.Count > 0
            || !Origin.TryTake(out var origin))
        {
            return false;
        }

        _ = ReturnTo(shell, origin);
        return true;
    }

    /// <summary>
    /// A route that no longer resolves — the member it named has since been removed — leaves the
    /// caregiver on the tab they are already looking at, which is somewhere real. The origin has
    /// been taken either way, so the next back press behaves as it would have before.
    /// </summary>
    /// <remarks>
    /// Returns a Task discarded by the caller rather than being <c>async void</c>. The two behave
    /// the same while the catch below is total, and differently the moment it is not: an escaping
    /// exception from <c>async void</c> is raised on the synchronisation context and takes the app
    /// down, which is the failure this whole file exists to prevent.
    /// </remarks>
    private static async Task ReturnTo(Shell shell, string origin)
    {
        try
        {
            await shell.GoToAsync(origin);
        }
        catch (Exception)
        {
            // Nothing to recover to, and nothing worth interrupting the caregiver about.
        }
    }
}
