using System.ComponentModel;

namespace CardiTrack.Mobile.Services;

/// <summary>
/// Keeps a page's content hidden until Android has given it the window insets, then fades it in.
/// </summary>
/// <remarks>
/// <para>
/// A page's first layout on Android runs before the system bars' insets are dispatched to it, so
/// its first frames draw as if there were no status bar and no gesture bar: the header under the
/// clock, the bottom bar down on the screen edge. The insets land a few frames later and
/// everything jumps into place — on the Dashboard, straight after sign-in, it read as an older
/// menu flashing up (recorded 2026-09-26: about half a second, every cold start).
/// </para>
/// <para>
/// Every page that uses this reserves the status bar on its root layout (SafeAreaEdges top
/// Container), so "the insets have landed" is observable: the root's first child moves down off
/// zero. Until then the root is transparent; after it, or after <see cref="Fallback"/> if the
/// move never comes (a device with no status bar inset), it fades in. Where the insets are already
/// known at the first layout — iOS, or any later visit — the child is already below zero and
/// nothing is held back.
/// </para>
/// </remarks>
internal static class InsetReveal
{
    /// <summary>The longest the content is held back before it is shown regardless.</summary>
    private static readonly TimeSpan Fallback = TimeSpan.FromMilliseconds(500);

    private const uint FadeMs = 120;

    /// <summary>Call from the page's constructor, after <c>InitializeComponent</c>.</summary>
    public static void HoldUntilInsetsApplied(this ContentPage page)
    {
        if (page.Content is not Layout root || root.Children.FirstOrDefault() is not VisualElement probe)
            return;

        var revealed = false;
        root.Opacity = 0;

        void Reveal()
        {
            if (revealed)
                return;
            revealed = true;
            probe.PropertyChanged -= OnProbeChanged;
            _ = root.FadeToAsync(1, FadeMs, Easing.CubicOut);
        }

        void OnProbeChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(VisualElement.Y) && probe.Y > 0)
                Reveal();
        }

        probe.PropertyChanged += OnProbeChanged;
        page.Loaded += (_, _) =>
        {
            if (probe.Y > 0)
                Reveal();
            else
                page.Dispatcher.DispatchDelayed(Fallback, Reveal);
        };
    }
}
