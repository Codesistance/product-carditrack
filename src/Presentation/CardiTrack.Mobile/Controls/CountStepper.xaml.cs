namespace CardiTrack.Mobile.Controls;

/// <summary>
/// A whole-number stepper drawn in the app's own language: a round outlined button either side of
/// the count, with the button that can do nothing dimmed. Stands in for the platform
/// <see cref="Stepper"/>, whose <see cref="ValueChanged"/> shape it keeps so a page can swap one
/// for the other without touching its handlers.
/// </summary>
/// <remarks>
/// <see cref="Value"/> is held inside <see cref="Minimum"/>..<see cref="Maximum"/> however it is
/// set — a tap at the edge is ignored, a programmatic value or a bound that moves past it is
/// clamped — and <see cref="ValueChanged"/> is raised for every change that sticks, including a
/// clamp. That is what the platform control does, and a page that sets the bounds and then the
/// value in a guarded refresh relies on it.
/// </remarks>
public partial class CountStepper : ContentView
{
    private const double DisabledOpacity = 0.35;

    public static readonly BindableProperty MinimumProperty = BindableProperty.Create(
        nameof(Minimum),
        typeof(int),
        typeof(CountStepper),
        0,
        propertyChanged: (bindable, _, _) => ((CountStepper)bindable).OnBoundsChanged());

    public static readonly BindableProperty MaximumProperty = BindableProperty.Create(
        nameof(Maximum),
        typeof(int),
        typeof(CountStepper),
        100,
        propertyChanged: (bindable, _, _) => ((CountStepper)bindable).OnBoundsChanged());

    public static readonly BindableProperty ValueProperty = BindableProperty.Create(
        nameof(Value),
        typeof(int),
        typeof(CountStepper),
        0,
        BindingMode.TwoWay,
        coerceValue: (bindable, value) => ((CountStepper)bindable).Clamp((int)value),
        propertyChanged: (bindable, oldValue, newValue) =>
            ((CountStepper)bindable).OnValueChanged((int)oldValue, (int)newValue));

    /// <summary>The lowest count the minus button will go to; it dims there.</summary>
    public int Minimum
    {
        get => (int)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    /// <summary>The highest count the plus button will go to; it dims there.</summary>
    public int Maximum
    {
        get => (int)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    /// <summary>The count shown. Always within <see cref="Minimum"/> and <see cref="Maximum"/>.</summary>
    public int Value
    {
        get => (int)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>Old and new count, in the same shape the platform stepper raises.</summary>
    public event EventHandler<ValueChangedEventArgs>? ValueChanged;

    public CountStepper()
    {
        InitializeComponent();
        Paint();
    }

    private void OnMinusTapped(object? sender, TappedEventArgs e)
    {
        if (Value > Minimum)
            Value--;
    }

    private void OnPlusTapped(object? sender, TappedEventArgs e)
    {
        if (Value < Maximum)
            Value++;
    }

    private void OnBoundsChanged()
    {
        // A bound that moved past the count drags the count with it, which re-enters
        // OnValueChanged and repaints; a bound that did not still changes which button is live.
        CoerceValue(ValueProperty);
        Paint();
    }

    private void OnValueChanged(int oldValue, int newValue)
    {
        Paint();
        ValueChanged?.Invoke(this, new ValueChangedEventArgs(oldValue, newValue));
    }

    /// <remarks>Min before Max, so a Minimum above the Maximum settles on the Maximum rather
    /// than throwing mid-layout while a page is still setting the two.</remarks>
    private int Clamp(int value) => Math.Min(Math.Max(value, Minimum), Maximum);

    private void Paint()
    {
        ValueLabel.Text = Value.ToString(System.Globalization.CultureInfo.CurrentCulture);

        var canDecrease = Value > Minimum;
        var canIncrease = Value < Maximum;

        MinusButton.Opacity = canDecrease ? 1 : DisabledOpacity;
        PlusButton.Opacity = canIncrease ? 1 : DisabledOpacity;

        SemanticProperties.SetDescription(MinusButton, canDecrease ? "Fewer" : "Fewer — already at the least");
        SemanticProperties.SetDescription(PlusButton, canIncrease ? "More" : "More — already at the most");
        SemanticProperties.SetDescription(ValueLabel, $"{Value}");
    }
}
