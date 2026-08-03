namespace CardiTrack.Mobile.Controls;

/// <summary>
/// Loading placeholder block with a gentle opacity pulse (M1-09a). The animation runs
/// only while the view is loaded so background pages don't leak timers.
/// </summary>
public sealed class SkeletonView : Border
{
    private const string AnimationName = "skeleton-pulse";

    public SkeletonView()
    {
        BackgroundColor = (Color)Microsoft.Maui.Controls.Application.Current!.Resources["SkeletonGray"];
        StrokeThickness = 0;
        StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 10 };
        Opacity = 0.85;

        Loaded += (_, _) => StartPulse();
        Unloaded += (_, _) => this.AbortAnimation(AnimationName);
    }

    private void StartPulse()
    {
        var pulse = new Animation
        {
            { 0.0, 0.5, new Animation(v => Opacity = v, 0.85, 0.35) },
            { 0.5, 1.0, new Animation(v => Opacity = v, 0.35, 0.85) },
        };
        pulse.Commit(this, AnimationName, length: 1100, repeat: () => IsLoaded);
    }
}
