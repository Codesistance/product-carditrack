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
    /// Only a jump from a pushed page records anything. Shell's stack always holds the section
    /// root at index 0, so a count above one means there is a page here that the jump is about to
    /// throw away — the Member Detail a caregiver was reading when they tapped the bell. From a
    /// tab root the jump costs nothing, and back should keep Android's own meaning.
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
    /// Shared by the platform back button and anything else that has to answer the same question.
    /// The navigation is started, not awaited — callers answer a synchronous "did you handle this".
    /// </remarks>
    public static bool TryReturnToOrigin()
    {
        if (Shell.Current is not { } shell
            || shell.Navigation.NavigationStack.Count > 1
            || !Origin.TryTake(out var origin))
        {
            return false;
        }

        ReturnTo(shell, origin);
        return true;
    }

    /// <summary>
    /// A route that no longer resolves — the member it named has since been removed — leaves the
    /// caregiver on the tab they are already looking at, which is somewhere real. The origin has
    /// been taken either way, so the next back press behaves as it would have before.
    /// </summary>
    private static async void ReturnTo(Shell shell, string origin)
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
