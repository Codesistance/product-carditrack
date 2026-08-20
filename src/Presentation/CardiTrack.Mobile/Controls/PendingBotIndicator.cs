using Microsoft.Maui.Controls.Shapes;

namespace CardiTrack.Mobile.Controls;

/// <summary>
/// The animated chatbot mark inside member chat's pending reply bubble — the same
/// icon_chatbot.svg the Dashboard launcher and the send button wear, breathing behind an
/// expanding pulse ring, so "the bot is working" reads as the same character the caregiver just
/// sent the question to. Follows <see cref="SkeletonView"/>'s discipline exactly: the loop runs
/// only while the view is loaded, so a dismissed overlay or a recycled bubble doesn't leak a
/// timer.
/// </summary>
public sealed class PendingBotIndicator : Grid
{
    private const string AnimationName = "pending-bot-pulse";

    private readonly Ellipse _ring;
    private readonly Image _mark;

    public PendingBotIndicator()
    {
        WidthRequest = 44;
        HeightRequest = 44;

        var accent = Microsoft.Maui.Controls.Application.Current?.Resources["Primary"] as Color ?? Colors.Blue;

        _ring = new Ellipse
        {
            WidthRequest = 34,
            HeightRequest = 34,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            Stroke = new SolidColorBrush(accent),
            StrokeThickness = 2,
            Opacity = 0,
            InputTransparent = true,
        };

        _mark = new Image
        {
            Source = "icon_chatbot.svg",
            WidthRequest = 28,
            HeightRequest = 28,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            InputTransparent = true,
        };

        Children.Add(_ring);
        Children.Add(_mark);

        Loaded += (_, _) => StartPulse();
        Unloaded += (_, _) => this.AbortAnimation(AnimationName);
    }

    private void StartPulse()
    {
        // One shared clock drives both marks so they can never drift: the ring blooms outward and
        // fades over the first three quarters, while the bot itself takes a slow breath.
        var pulse = new Animation
        {
            { 0.00, 0.75, new Animation(v => _ring.Scale = v, 0.7, 1.35) },
            { 0.00, 0.15, new Animation(v => _ring.Opacity = v, 0.0, 0.55) },
            { 0.15, 0.75, new Animation(v => _ring.Opacity = v, 0.55, 0.0) },
            { 0.00, 0.50, new Animation(v => _mark.Scale = v, 1.0, 1.10, Easing.SinInOut) },
            { 0.50, 1.00, new Animation(v => _mark.Scale = v, 1.10, 1.0, Easing.SinInOut) },
        };
        pulse.Commit(this, AnimationName, length: 1400, repeat: () => IsLoaded);
    }
}
