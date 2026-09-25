namespace CardiTrack.Mobile.Controls;

public enum CompactActionTone
{
    Blue,
    Red,
}

/// <summary>
/// The device card's small tinted action — see the XAML for why it is a pill inside a larger
/// tap area rather than a <see cref="Button"/> sized down.
/// </summary>
/// <remarks>
/// Not a <see cref="Button"/>: a native button draws its press ripple over its whole bounds,
/// which would light up the 44-high tap band around the 30-high pill. Announced as a button all
/// the same, with its caption as its name.
/// </remarks>
public partial class CompactActionButton : ContentView
{
    public event EventHandler? Clicked;

    public CompactActionButton()
    {
        InitializeComponent();
        AutomationProperties.SetIsInAccessibleTree(this, true);
        ApplyTone();
    }

    public string Text
    {
        get => Caption.Text;
        set
        {
            Caption.Text = value;
            SemanticProperties.SetDescription(this, value);
        }
    }

    public string? IconSource
    {
        set => Icon.Source = value;
    }

    private CompactActionTone _tone;

    public CompactActionTone Tone
    {
        get => _tone;
        set
        {
            _tone = value;
            ApplyTone();
        }
    }

    private void ApplyTone()
    {
        var resources = Microsoft.Maui.Controls.Application.Current!.Resources;
        var (fill, ink) = _tone == CompactActionTone.Red
            ? ("ActionTintRedBackground", "ActionTintRedInk")
            : ("ActionTintBlueBackground", "ActionTintBlueInk");
        Fill.BackgroundColor = (Color)resources[fill];
        Caption.TextColor = (Color)resources[ink];
    }

    protected override void OnPropertyChanged(string? propertyName = null)
    {
        base.OnPropertyChanged(propertyName);
        // The ActionButton styles' disabled look, on the pill alone.
        if (propertyName == nameof(IsEnabled))
            Fill.Opacity = IsEnabled ? 1 : 0.45;
    }

    private async void OnTapped(object? sender, TappedEventArgs e)
    {
        if (!IsEnabled)
            return;

        Clicked?.Invoke(this, EventArgs.Empty);

        // The press feedback a gesture doesn't give: a brief dip, as the ActionButton's pressed
        // state does.
        await Fill.FadeToAsync(0.7, 70);
        await Fill.FadeToAsync(IsEnabled ? 1 : 0.45, 120);
    }
}
