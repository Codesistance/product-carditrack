namespace CardiTrack.Mobile.Services;

/// <summary>
/// What a screen's back arrow does: step back through the app's own history when there is a page
/// behind this one, and otherwise go to the screen the arrow names.
/// </summary>
/// <remarks>
/// <para>
/// Every back arrow used to be one or the other, chosen per page and fixed. Naming a destination
/// was the safe choice — Member Detail and Device Management are both reachable from the
/// Notifications inbox as well as from the dashboard, and <c>".."</c> on a page with nothing
/// underneath it goes nowhere — but it costs the caregiver their place: entering Member Detail
/// from the inbox and pressing back landed them on the dashboard, with the list they were working
/// through gone. It also disagreed with Android's own back button, which pops the stack.
/// </para>
/// <para>
/// So: history first, destination as the floor. The destination is still what makes the arrow
/// safe — it is what a tab root, a deep link, or a notification tap falls back to — but it is no
/// longer what happens when there is real history to honour.
/// </para>
/// </remarks>
internal static class BackNavigation
{
    /// <summary>
    /// Pops one page when this one was pushed within the app; otherwise navigates to
    /// <paramref name="destination"/>.
    /// </summary>
    /// <param name="destination">
    /// Where the arrow goes with no history behind it — an absolute route, so it selects its tab
    /// rather than pushing another copy of the page onto wherever the user happens to be.
    /// </param>
    /// <remarks>
    /// The middle case is a tab root the caregiver was <em>sent</em> to: the stack is empty because
    /// an absolute route emptied it, not because they started here. <see cref="TabNavigation"/>
    /// kept what that jump dropped, and returning there beats the named destination, which would
    /// send someone who arrived from Member Detail to the dashboard instead of back.
    /// </remarks>
    public static Task GoBackAsync(this Page page, string destination)
    {
        if (Shell.Current is not { } shell)
            return Task.CompletedTask;

        if (HasPageBehind(shell))
            return shell.GoToAsync("..");

        return TabNavigation.Origin.TryTake(out var origin)
            ? shell.GoToAsync(origin)
            : shell.GoToAsync(destination);
    }

    /// <summary>
    /// Whether anything of the app's own sits under the current page. Shell's navigation stack
    /// always holds the section's root page at index 0, so a count above one means the page on
    /// screen was pushed on top of something and has somewhere to pop back to. A tab root — Alerts,
    /// the dashboard — is a count of one and falls back to its named destination.
    /// </summary>
    private static bool HasPageBehind(Shell shell) =>
        shell.Navigation.NavigationStack.Count > 1;
}
