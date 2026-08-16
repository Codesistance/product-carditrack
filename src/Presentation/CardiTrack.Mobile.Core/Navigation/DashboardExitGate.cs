namespace CardiTrack.Mobile.Core.Navigation;

/// <summary>
/// Whether a back press is happening at the dashboard tab root — the only place a second
/// swipe is required before the app finishes.
/// </summary>
public static class DashboardExitGate
{
    /// <summary>
    /// True when the caregiver is looking at the dashboard itself, not a page pushed above it
    /// and not a modal over it. Location is Shell's <c>CurrentState.Location</c>; a trailing
    /// slash is tolerated, extra segments (Member Detail) are not.
    /// </summary>
    public static bool IsDashboardTabRoot(string? location, int navigationStackCount, int modalStackCount)
    {
        if (navigationStackCount > 1 || modalStackCount > 0)
            return false;

        if (string.IsNullOrEmpty(location))
            return false;

        const string route = "//dashboard";
        return location.Equals(route, StringComparison.OrdinalIgnoreCase)
            || location.Equals(route + "/", StringComparison.OrdinalIgnoreCase);
    }
}
