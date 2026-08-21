namespace CardiTrack.Mobile.Controls;

/// <summary>
/// The gradient header band shared by every screen that has a header (Figma 101:2872).
/// Content goes inside it; the band supplies the gradient, the right-edge feather, the bottom
/// rounding and the shadow.
/// </summary>
public partial class HeaderBand : ContentView
{
    /// <summary>
    /// Inset around the band's content. Only the padding varies between screens — a wizard
    /// header clears the status bar and carries a progress pill, where the dashboard's band is
    /// compact — so the gradient, radius and shadow stay fixed and the chrome reads as one thing.
    /// </summary>
    public static readonly BindableProperty ContentPaddingProperty =
        BindableProperty.Create(
            nameof(ContentPadding),
            typeof(Thickness),
            typeof(HeaderBand),
            new Thickness(20, 14, 20, 26));

    /// <summary>
    /// Draws the brand mark oversized and nearly transparent, bled off the band's outer corner.
    /// </summary>
    /// <remarks>
    /// Off by default and on for the three screens that sign a caregiver in — those bands carry a
    /// title and one line under it and nothing else, so they read as empty gradient with words in
    /// the corner. Every other band in the app has a member, a date, a filter row or a progress
    /// pill sitting in it; putting the mark behind those would be decorating a working surface,
    /// and the app would be telling someone already inside it whose app it is.
    /// </remarks>
    public static readonly BindableProperty ShowWatermarkProperty =
        BindableProperty.Create(nameof(ShowWatermark), typeof(bool), typeof(HeaderBand), false);

    public HeaderBand()
    {
        InitializeComponent();
    }

    public Thickness ContentPadding
    {
        get => (Thickness)GetValue(ContentPaddingProperty);
        set => SetValue(ContentPaddingProperty, value);
    }

    public bool ShowWatermark
    {
        get => (bool)GetValue(ShowWatermarkProperty);
        set => SetValue(ShowWatermarkProperty, value);
    }
}
