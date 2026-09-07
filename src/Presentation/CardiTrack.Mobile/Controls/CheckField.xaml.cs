namespace CardiTrack.Mobile.Controls;

/// <summary>
/// A tick box with its label, drawn the same on both platforms. Stands in for the platform
/// <see cref="CheckBox"/>, whose <see cref="IsChecked"/> and <see cref="CheckedChanged"/> it
/// keeps so a page swaps one for the other without touching its handlers.
/// </summary>
/// <remarks>
/// <para>
/// The CheckBox was the one control on these forms that each OS drew its own way — Material's
/// box on Android, a circle on iOS — while everything around it was the app's. This one is a
/// rounded square in the field hairline when off and the accent when on, with a 44pt target
/// around a 22pt box so it can be hit without being loud.
/// </para>
/// <para>
/// The label is part of the control so that tapping it ticks the box, which is what a caregiver
/// expects of a label beside a box and what two pages were wiring by hand. Pass plain words as
/// <see cref="Text"/>; a label with its own links inside it — the sign-up form's terms line —
/// goes in as content instead, and keeps its own taps.
/// </para>
/// </remarks>
[ContentProperty(nameof(LabelContent))]
public partial class CheckField : ContentView
{
    private const double DisabledOpacity = 0.5;

    public static readonly BindableProperty IsCheckedProperty = BindableProperty.Create(
        nameof(IsChecked),
        typeof(bool),
        typeof(CheckField),
        false,
        BindingMode.TwoWay,
        propertyChanged: (bindable, _, newValue) => ((CheckField)bindable).OnCheckedChanged((bool)newValue));

    public static readonly BindableProperty TextProperty = BindableProperty.Create(
        nameof(Text),
        typeof(string),
        typeof(CheckField),
        null,
        propertyChanged: (bindable, _, _) => ((CheckField)bindable).Paint());

    public static readonly BindableProperty TextColorProperty = BindableProperty.Create(
        nameof(TextColor),
        typeof(Color),
        typeof(CheckField),
        null,
        propertyChanged: (bindable, _, _) => ((CheckField)bindable).Paint());

    public static readonly BindableProperty CheckedColorProperty = BindableProperty.Create(
        nameof(CheckedColor),
        typeof(Color),
        typeof(CheckField),
        null,
        propertyChanged: (bindable, _, _) => ((CheckField)bindable).Paint());

    public static readonly BindableProperty LabelContentProperty = BindableProperty.Create(
        nameof(LabelContent),
        typeof(View),
        typeof(CheckField),
        null,
        propertyChanged: (bindable, _, newValue) => ((CheckField)bindable).OnLabelContentChanged((View?)newValue));

    public CheckField()
    {
        InitializeComponent();
        Paint();
    }

    /// <summary>Whether the box is ticked.</summary>
    public bool IsChecked
    {
        get => (bool)GetValue(IsCheckedProperty);
        set => SetValue(IsCheckedProperty, value);
    }

    /// <summary>The words beside the box. Tapping them ticks it.</summary>
    public string? Text
    {
        get => (string?)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>Ink for <see cref="Text"/>; the label's style decides when unset.</summary>
    public Color? TextColor
    {
        get => (Color?)GetValue(TextColorProperty);
        set => SetValue(TextColorProperty, value);
    }

    /// <summary>The fill and edge of a ticked box. Primary unless the page says otherwise — the delete-account row says DangerRed.</summary>
    public Color? CheckedColor
    {
        get => (Color?)GetValue(CheckedColorProperty);
        set => SetValue(CheckedColorProperty, value);
    }

    /// <summary>
    /// A label of the page's own in place of <see cref="Text"/>, for a line that carries links.
    /// Its taps are its own, so it does not tick the box.
    /// </summary>
    public View? LabelContent
    {
        get => (View?)GetValue(LabelContentProperty);
        set => SetValue(LabelContentProperty, value);
    }

    /// <summary>Raised whenever <see cref="IsChecked"/> changes, in the shape the platform box raises.</summary>
    public event EventHandler<CheckedChangedEventArgs>? CheckedChanged;

    protected override void OnPropertyChanged(string? propertyName = null)
    {
        base.OnPropertyChanged(propertyName);

        if (propertyName == IsEnabledProperty.PropertyName)
            Opacity = IsEnabled ? 1 : DisabledOpacity;
        else if (propertyName == SemanticProperties.HintProperty.PropertyName)
            SemanticProperties.SetHint(BoxHit, SemanticProperties.GetHint(this));
    }

    private void OnTapped(object? sender, TappedEventArgs e)
    {
        if (IsEnabled)
            IsChecked = !IsChecked;
    }

    private void OnCheckedChanged(bool isChecked)
    {
        Paint();
        CheckedChanged?.Invoke(this, new CheckedChangedEventArgs(isChecked));
    }

    private void OnLabelContentChanged(View? content)
    {
        TextLabel.IsVisible = content is null;
        // Only one label lives in the host at a time: the built-in one, or the page's.
        for (var i = LabelHost.Count - 1; i >= 0; i--)
        {
            if (!ReferenceEquals(LabelHost[i], TextLabel))
                LabelHost.RemoveAt(i);
        }
        if (content is not null)
            LabelHost.Add(content);
    }

    private void Paint()
    {
        var accent = CheckedColor ?? MetricStatus.Resource("Primary", Colors.Blue);
        var on = IsChecked;

        Box.BackgroundColor = on ? accent : MetricStatus.Resource("White", Colors.White);
        Box.Stroke = new SolidColorBrush(on ? accent : MetricStatus.Resource("InputBorder", Colors.LightGray));
        Tick.IsVisible = on;

        TextLabel.Text = Text;
        if (TextColor is { } ink)
            TextLabel.TextColor = ink;

        var name = Text ?? "Tick box";
        SemanticProperties.SetDescription(BoxHit, $"{name}, {(on ? "ticked" : "not ticked")}");
    }
}
