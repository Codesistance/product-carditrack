using System.ComponentModel;

namespace CardiTrack.Mobile.Controls;

/// <summary>
/// Generic collapsible section — header (title + expand hint + chevron) toggling an arbitrary
/// body. Used to tuck Key Metrics behind a tap so a CardiMember card stays compact once more than
/// one can appear on the dashboard.
/// </summary>
[ContentProperty(nameof(Body))]
public partial class AccordionSection : ContentView
{
    private const uint AnimationLengthMs = 200;

    private bool _isAnimating;
    private View? _body;

    public bool IsExpanded { get; private set; }

    public string HeaderText
    {
        set => HeaderLabel.Text = value;
    }

    /// <summary>The collapsible content. Set once, declaratively, as this control's XAML children.</summary>
    public View? Body
    {
        get => _body;
        set
        {
            _body = value;
            BodyHost.Content = value;
        }
    }

    public AccordionSection()
    {
        InitializeComponent();
    }

    private void OnHeaderTapped(object? sender, TappedEventArgs e)
    {
        if (_isAnimating)
            return;

        if (IsExpanded)
            Collapse();
        else
            Expand();
    }

    private void Expand()
    {
        _isAnimating = true;
        IsExpanded = true;
        HintLabel.IsVisible = false;

        var width = RootLayout.Width > 0 ? RootLayout.Width : Width;
        var targetHeight = BodyHost.Measure(width, double.PositiveInfinity).Height;

        this.AbortAnimation("accordion");
        new Animation(v => BodyClip.HeightRequest = v, BodyClip.Height, targetHeight)
            .Commit(this, "accordion", 16, AnimationLengthMs, Easing.CubicOut, (_, _) => _isAnimating = false);

        _ = ChevronIcon.RotateToAsync(180, AnimationLengthMs, Easing.CubicOut);
    }

    private void Collapse()
    {
        _isAnimating = true;
        IsExpanded = false;

        this.AbortAnimation("accordion");
        new Animation(v => BodyClip.HeightRequest = v, BodyClip.Height, 0)
            .Commit(this, "accordion", 16, AnimationLengthMs, Easing.CubicIn, (_, _) =>
            {
                _isAnimating = false;
                HintLabel.IsVisible = true;
            });

        _ = ChevronIcon.RotateToAsync(0, AnimationLengthMs, Easing.CubicIn);
    }
}
