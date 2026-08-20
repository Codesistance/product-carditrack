using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Services;

namespace CardiTrack.Mobile;

/// <summary>
/// Shows <see cref="MemberChatPage"/> as an overlay layered into the host page's own root grid —
/// not a modal push. MAUI's modal push composites the new page as a fully opaque screen on
/// Android, so a "transparent" pushed page never actually shows the previous page dimmed behind
/// it; layering into the host's visual tree is the only way the background genuinely stays
/// visible. The overlay spans every row of the host grid, so it dims the header and nav bar too.
/// </summary>
internal static class MemberChatLauncher
{
    public static void ShowOverlay(Grid hostRoot, Guid memberId, string? memberFirstName)
    {
        // One at a time: a second tap while the overlay is up must not stack another.
        if (hostRoot.Children.OfType<MemberChatPage>().Any())
            return;

        var overlay = new MemberChatPage(
            ServiceHelper.GetRequiredService<ICardiTrackApiClient>(), memberId, memberFirstName);

        Grid.SetRow(overlay, 0);
        Grid.SetRowSpan(overlay, Math.Max(1, hostRoot.RowDefinitions.Count));

        overlay.CloseRequested += (_, _) => hostRoot.Children.Remove(overlay);
        hostRoot.Children.Add(overlay);
    }
}
