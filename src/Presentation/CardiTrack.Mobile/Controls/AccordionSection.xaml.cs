using System.Globalization;
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
    /// <summary>
    /// How many things are folded behind the chevron, drawn as a superscript beside the title.
    /// Null or zero draws nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The weakness of putting anything behind a chevron is that the chevron says nothing about
    /// whether opening it is worth doing. A caregiver checking on someone should not have to open
    /// a section to find out whether there is anything in it.
    /// </para>
    /// <para>
    /// Announced as words rather than as a bare numeral, because a screen reader reading "Trends
    /// to keep an eye on, 3" leaves the 3 to be guessed at.
    /// </para>
    /// </remarks>
    public int? Count
    {
        set
        {
            var count = value.GetValueOrDefault();
            CountBadge.IsVisible = count > 0;
            CountLabel.Text = count > 0 ? count.ToString(CultureInfo.InvariantCulture) : string.Empty;

            AutomationProperties.SetName(
                HeaderShell,
                count switch
                {
                    0 => HeaderLabel.Text,
                    1 => $"{HeaderLabel.Text}, 1 to look at",
                    _ => $"{HeaderLabel.Text}, {count} to look at",
                });
        }
    }

    /// <summary>
    /// What colour the count badge takes. Defaults to the neutral ink where a caller sets a count
    /// without saying what kind of thing it is counting.
    /// </summary>
    /// <remarks>
    /// The caller's, not this control's: a section knows how many things are behind it, but only
    /// the page filling it knows whether they are things to worry about. Passing the colour keeps
    /// the badge and whatever the body draws from disagreeing about that.
    /// </remarks>
    public Color? CountTint
    {
        set => CountBadge.BackgroundColor =
            value ?? NamedColor("CountBadgeNeutral") ?? Colors.Gray;
    }

    private static Color? NamedColor(string key) =>
        Microsoft.Maui.Controls.Application.Current?.Resources.TryGetValue(key, out var found) == true
            ? found as Color
            : null;

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
        ApplyHeaderChrome();
    }

    /// <summary>
    /// The closed header's tint, and its absence once the body is open.
    /// </summary>
    /// <remarks>
    /// Only <see cref="VisualElement.BackgroundColor"/> moves. The <c>Border</c> around the
    /// header keeps its padding and its cancelling margin in both states, so nothing reflows when
    /// the fill appears or goes — which is what makes this a style the closed state can have
    /// without the open one paying for it.
    /// </remarks>
    private void ApplyHeaderChrome() =>
        HeaderChrome.BackgroundColor = IsExpanded
            ? Colors.Transparent
            : NamedColor("InputBackground") ?? Colors.Transparent;

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
        ApplyHeaderChrome();

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
        ApplyHeaderChrome();

        this.AbortAnimation("accordion");
        new Animation(v => BodyClip.HeightRequest = v, BodyClip.Height, 0)
            .Commit(this, "accordion", 16, AnimationLengthMs, Easing.CubicIn, (_, _) =>
            {
                _isAnimating = false;
            });

        _ = ChevronIcon.RotateToAsync(0, AnimationLengthMs, Easing.CubicIn);
    }
}
