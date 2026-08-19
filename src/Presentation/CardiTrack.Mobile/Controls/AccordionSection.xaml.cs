namespace CardiTrack.Mobile.Controls;

/// <summary>
/// Generic collapsible section — header (title + chevron) toggling an arbitrary
/// body. Used to tuck Key Metrics behind a tap so a CardiMember card stays compact once more than
/// one can appear on the dashboard.
/// </summary>
/// <remarks>
/// Deliberately NOT marked <c>[ContentProperty(nameof(Body))]</c>: that attribute would also
/// govern this control's own XAML, so loading AccordionSection.xaml would assign its root layout
/// to <see cref="Body"/> — dereferencing <c>BodyHost</c> before the layout that contains it
/// exists, which throws. Callers name the property explicitly instead
/// (<c>&lt;controls:AccordionSection.Body&gt;</c>).
/// </remarks>
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

    /// <summary>
    /// The header's type style, for pages whose section titles are not <c>Heading2</c> — the
    /// default this control carries for the dashboard card it was written for.
    /// </summary>
    public Style HeaderStyle
    {
        set => HeaderLabel.Style = value;
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

    public static readonly BindableProperty IconSourceProperty = BindableProperty.Create(
        nameof(IconSource),
        typeof(string),
        typeof(AccordionSection),
        null,
        propertyChanged: (bindable, _, value) =>
        {
            var section = (AccordionSection)bindable;
            section.HeaderIconImage.Source = value as string;
            section.HeaderIconImage.IsVisible = !string.IsNullOrEmpty(value as string);
        });

    /// <summary>The header row's leading glyph — the dropdowns' 22-unit icon. Absent, the row
    /// starts at the label, and the icon column collapses.</summary>
    public string? IconSource
    {
        get => (string?)GetValue(IconSourceProperty);
        set => SetValue(IconSourceProperty, value);
    }

    public AccordionSection()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Re-fits an open body to content that arrived after it was opened. The clip is held at a
    /// height measured once, when the body was empty or still a skeleton, so a body that fills
    /// itself from a background load would otherwise be sliced off at whatever it was worth then.
    /// A no-op while closed or mid-animation: both already end at the right height.
    /// </summary>
    public void RefreshHeight()
    {
        if (!IsExpanded || _isAnimating)
            return;

        var width = RootLayout.Width > 0 ? RootLayout.Width : Width;
        if (width <= 0)
            return;

        BodyClip.HeightRequest = BodyHost.Measure(width, double.PositiveInfinity).Height;
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
        SemanticProperties.SetDescription(ChevronIcon, "Collapse");

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
        SemanticProperties.SetDescription(ChevronIcon, "Expand");

        this.AbortAnimation("accordion");
        new Animation(v => BodyClip.HeightRequest = v, BodyClip.Height, 0)
            .Commit(this, "accordion", 16, AnimationLengthMs, Easing.CubicIn, (_, _) =>
            {
                _isAnimating = false;
            });

        _ = ChevronIcon.RotateToAsync(0, AnimationLengthMs, Easing.CubicIn);
    }
}
