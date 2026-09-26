using System.Windows.Input;

namespace CardiTrack.Mobile.Controls;

/// <summary>What a button does, which decides its colour — one colour per meaning.</summary>
public enum AppButtonTone
{
    /// <summary>The thing the screen is for: the brand gradient at L, the action blue below it.</summary>
    Primary,

    /// <summary>A real alternative to the primary: white with a PrimaryDark outline.</summary>
    Secondary,

    /// <summary>Backing out — Cancel, Close, Later: dark grey.</summary>
    Neutral,

    /// <summary>An everyday action inside a card or row, or an undo: pale blue with blue ink.</summary>
    Tonal,

    /// <summary>Removal and anything that cannot be undone: red, pale red with red ink at S.</summary>
    Danger,

    /// <summary>A quiet way out or a small link-like action: blue text, no fill.</summary>
    Text,
}

/// <summary>How much room a button takes: 48 for a page's call to action, 36 in sheets, popups
/// and cards, 30 inside a row. Every size still takes taps across 44.</summary>
public enum AppButtonSize
{
    L,
    M,
    S,
}

/// <summary>
/// The app's single button, replacing the dozen styles and hand-drawn variants that had grown up
/// for the same few jobs (the 2026-09-26 button audit): six tones, three sizes, one radius, one
/// pressed and one disabled look.
/// </summary>
/// <remarks>
/// Not a <see cref="Button"/>. A native button is floored to 44 high on Android by the implicit
/// style and draws its press ripple across its whole bounds, so a 30-high button could only be
/// had by making it 44 — or by drawing it, as the device card's compact buttons did first. This draws
/// every size the same way: the shape at the size's height, the tap across a 44 band. Announced as
/// a button all the same, with its caption as its name.
/// </remarks>
public partial class AppButton : ContentView
{
    public static readonly BindableProperty TextProperty = BindableProperty.Create(
        nameof(Text), typeof(string), typeof(AppButton), string.Empty,
        propertyChanged: (b, _, n) => ((AppButton)b).ApplyText((string?)n));

    public static readonly BindableProperty IconProperty = BindableProperty.Create(
        nameof(Icon), typeof(ImageSource), typeof(AppButton),
        propertyChanged: (b, _, _) => ((AppButton)b).ApplyLook());

    public static readonly BindableProperty ToneProperty = BindableProperty.Create(
        nameof(Tone), typeof(AppButtonTone), typeof(AppButton), AppButtonTone.Primary,
        propertyChanged: (b, _, _) => ((AppButton)b).ApplyLook());

    public static readonly BindableProperty SizeProperty = BindableProperty.Create(
        nameof(Size), typeof(AppButtonSize), typeof(AppButton), AppButtonSize.M,
        propertyChanged: (b, _, _) => ((AppButton)b).ApplyLook());

    public static readonly BindableProperty IsDimmedProperty = BindableProperty.Create(
        nameof(IsDimmed), typeof(bool), typeof(AppButton), false,
        propertyChanged: (b, _, _) => ((AppButton)b).ApplyOpacity());

    public static readonly BindableProperty CommandProperty = BindableProperty.Create(
        nameof(Command), typeof(ICommand), typeof(AppButton));

    public static readonly BindableProperty CommandParameterProperty = BindableProperty.Create(
        nameof(CommandParameter), typeof(object), typeof(AppButton));

    public event EventHandler? Clicked;

    public AppButton()
    {
        InitializeComponent();
        AutomationProperties.SetIsInAccessibleTree(this, true);
        ApplyLook();
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>The glyph before the caption — the white <c>icon_btn_*</c> set on the solid tones,
    /// the <c>_tint</c> set on Tonal. Optional.</summary>
    public ImageSource? Icon
    {
        get => (ImageSource?)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public AppButtonTone Tone
    {
        get => (AppButtonTone)GetValue(ToneProperty);
        set => SetValue(ToneProperty, value);
    }

    public AppButtonSize Size
    {
        get => (AppButtonSize)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    /// <summary>
    /// Drawn as disabled while staying tappable — for a form's submit button, which looks unready
    /// until the form is complete but still answers a tap by saying what is missing. A button that
    /// really cannot act uses <see cref="VisualElement.IsEnabled"/> instead.
    /// </summary>
    public bool IsDimmed
    {
        get => (bool)GetValue(IsDimmedProperty);
        set => SetValue(IsDimmedProperty, value);
    }

    public ICommand? Command
    {
        get => (ICommand?)GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    public object? CommandParameter
    {
        get => GetValue(CommandParameterProperty);
        set => SetValue(CommandParameterProperty, value);
    }

    /// <summary>The visible heights, by size.</summary>
    public static double HeightOf(AppButtonSize size) => size switch
    {
        AppButtonSize.L => 48,
        AppButtonSize.S => 30,
        _ => 36,
    };

    private void ApplyText(string? text)
    {
        Caption.Text = text;
        SemanticProperties.SetDescription(this, text);
    }

    private void ApplyLook()
    {
        var size = Size;
        var (fontSize, padding, glyph, gap) = size switch
        {
            AppButtonSize.L => (16d, 20d, 20d, 8d),
            AppButtonSize.S => (13d, 10d, 16d, 5d),
            _ => (14d, 14d, 18d, 6d),
        };

        Fill.HeightRequest = HeightOf(size);
        Fill.Padding = new Thickness(Tone == AppButtonTone.Text ? 4 : padding, 0);
        HeightRequest = Math.Max(44, HeightOf(size));
        Caption.FontSize = fontSize;
        Glyph.WidthRequest = glyph;
        Glyph.HeightRequest = glyph;
        Row.Spacing = gap;
        Glyph.Source = Icon;
        Glyph.IsVisible = Icon is not null;

        Fill.Background = null;
        Fill.BackgroundColor = Colors.Transparent;
        Fill.Stroke = null;
        Fill.StrokeThickness = 0;

        switch (Tone)
        {
            case AppButtonTone.Primary:
                Fill.Background = Resource<Brush>(size == AppButtonSize.L ? "GradientButtonBrush" : "ActionBlueBrush");
                Caption.TextColor = Resource<Color>("White");
                break;
            case AppButtonTone.Secondary:
                Fill.BackgroundColor = Resource<Color>("White");
                Fill.Stroke = Resource<Color>("PrimaryDark");
                Fill.StrokeThickness = 1;
                Caption.TextColor = Resource<Color>("Primary");
                break;
            case AppButtonTone.Neutral:
                Fill.Background = Resource<Brush>("ActionDarkBrush");
                Caption.TextColor = Resource<Color>("White");
                break;
            case AppButtonTone.Tonal:
                Fill.BackgroundColor = Resource<Color>("ActionTintBlueBackground");
                Caption.TextColor = Resource<Color>("ActionTintBlueInk");
                break;
            case AppButtonTone.Danger when size == AppButtonSize.S:
                Fill.BackgroundColor = Resource<Color>("ActionTintRedBackground");
                Caption.TextColor = Resource<Color>("ActionTintRedInk");
                break;
            case AppButtonTone.Danger:
                Fill.Background = Resource<Brush>("ActionRedBrush");
                Caption.TextColor = Resource<Color>("White");
                break;
            case AppButtonTone.Text:
                Caption.TextColor = Resource<Color>("Primary");
                break;
        }

        ApplyOpacity();
    }

    private double RestingOpacity => IsEnabled && !IsDimmed ? 1 : 0.45;

    private void ApplyOpacity() => Fill.Opacity = RestingOpacity;

    protected override void OnPropertyChanged(string? propertyName = null)
    {
        base.OnPropertyChanged(propertyName);
        if (propertyName == nameof(IsEnabled))
            ApplyOpacity();
    }

    private async void OnTapped(object? sender, TappedEventArgs e)
    {
        if (!IsEnabled)
            return;

        Clicked?.Invoke(this, EventArgs.Empty);
        if (Command is { } command && command.CanExecute(CommandParameter))
            command.Execute(CommandParameter);

        // The press feedback a gesture does not give on its own: a brief dip.
        await Fill.FadeToAsync(0.85, 70);
        await Fill.FadeToAsync(RestingOpacity, 120);
    }

    private static T Resource<T>(string key) =>
        (T)Microsoft.Maui.Controls.Application.Current!.Resources[key];
}
