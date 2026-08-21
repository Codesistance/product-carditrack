using Microsoft.Maui.Controls.Shapes;

namespace CardiTrack.Mobile.Controls;

/// <summary>
/// A small solid dot that blooms an expanding ring behind it, on a loop — "something new is here"
/// without a count, the pulsing counterpart to <c>DashboardHeader</c>'s static <c>NudgeDot</c>.
/// First use: the Dashboard hero card's corner badge for an available Advise suggestion. Follows
/// <see cref="PendingBotIndicator"/>'s discipline exactly: the loop runs only while the view is
/// loaded, so a card that scrolls off screen or gets rebuilt doesn't leak a timer.
/// </summary>
public sealed class PulseDot : Grid
{
    private const string AnimationName = "pulse-dot";

    private readonly Ellipse _ring;

    public PulseDot()
    {
        WidthRequest = 24;
        HeightRequest = 24;
        InputTransparent = true;

        var accent = Microsoft.Maui.Controls.Application.Current?.Resources["Primary"] as Color ?? Colors.Blue;

        _ring = new Ellipse
        {
            WidthRequest = 14,
            HeightRequest = 14,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            Stroke = new SolidColorBrush(accent),
            StrokeThickness = 2,
            Opacity = 0,
        };

        var dot = new Ellipse
        {
            WidthRequest = 10,
            HeightRequest = 10,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            Fill = new SolidColorBrush(accent),
            Stroke = new SolidColorBrush(Colors.White),
            StrokeThickness = 2,
        };

        Children.Add(_ring);
        Children.Add(dot);

        Loaded += (_, _) => StartPulse();
        Unloaded += (_, _) => this.AbortAnimation(AnimationName);
    }

    private void StartPulse()
    {
        var pulse = new Animation
        {
            { 0.00, 0.75, new Animation(v => _ring.Scale = v, 0.7, 1.6) },
            { 0.00, 0.15, new Animation(v => _ring.Opacity = v, 0.0, 0.6) },
            { 0.15, 0.75, new Animation(v => _ring.Opacity = v, 0.6, 0.0) },
        };
        pulse.Commit(this, AnimationName, length: 1600, repeat: () => IsLoaded);
    }
}
