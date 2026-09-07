using CardiTrack.Mobile.Core.Forms;
using CardiTrack.Mobile.Services;

namespace CardiTrack.Mobile.Controls;

/// <summary>
/// A single choice from a short list, drawn as one of the app's own fields and opened into the
/// app's own sheet. Stands in for the platform <see cref="Picker"/>, whose surface it keeps —
/// <see cref="ItemsSource"/>, <see cref="SelectedIndex"/>, <see cref="SelectedItem"/>,
/// <see cref="Title"/> as the placeholder, <see cref="SelectedIndexChanged"/> — so a page swaps
/// one for the other without touching its handlers.
/// </summary>
/// <remarks>
/// <para>
/// The Picker drew itself as a system underline with a system list behind it, in a form where
/// every other field is a rounded white box in Quicksand. It also showed a caregiver nothing
/// about what was already set until the list was open. The sheet marks the current row, so what
/// is set is read before it is changed.
/// </para>
/// <para>
/// The selection rule lives in <see cref="ChoiceSelection"/>, where it is tested: an index is
/// held inside the list, a list swap clears it, and <see cref="SelectedIndexChanged"/> is raised
/// only when the index actually moved. The alarm builder rebuilds its lists on every edit and
/// re-selects into them, and would loop on a control that announced every rebuild.
/// </para>
/// </remarks>
public partial class ChoiceField : ContentView
{
    private const double DisabledOpacity = 0.5;

    public static readonly BindableProperty ItemsSourceProperty = BindableProperty.Create(
        nameof(ItemsSource),
        typeof(IList<string>),
        typeof(ChoiceField),
        null,
        propertyChanged: (bindable, _, newValue) =>
            ((ChoiceField)bindable).OnOptionsChanged((IList<string>?)newValue));

    public static readonly BindableProperty SelectedIndexProperty = BindableProperty.Create(
        nameof(SelectedIndex),
        typeof(int),
        typeof(ChoiceField),
        -1,
        BindingMode.TwoWay,
        propertyChanged: (bindable, _, newValue) =>
            ((ChoiceField)bindable).OnIndexRequested((int)newValue));

    public static readonly BindableProperty SelectedItemProperty = BindableProperty.Create(
        nameof(SelectedItem),
        typeof(string),
        typeof(ChoiceField),
        null,
        BindingMode.TwoWay,
        propertyChanged: (bindable, _, newValue) =>
            ((ChoiceField)bindable).OnItemRequested((string?)newValue));

    public static readonly BindableProperty TitleProperty = BindableProperty.Create(
        nameof(Title),
        typeof(string),
        typeof(ChoiceField),
        null,
        propertyChanged: (bindable, _, _) => ((ChoiceField)bindable).Paint());

    public static readonly BindableProperty PromptProperty = BindableProperty.Create(
        nameof(Prompt),
        typeof(string),
        typeof(ChoiceField),
        null,
        propertyChanged: (bindable, _, _) => ((ChoiceField)bindable).Paint());

    private readonly ChoiceSelection _selection = new();

    /// <summary>Set while the bindable properties are being brought in line with the selection.</summary>
    private bool _mirroring;

    /// <summary>Set while the sheet is open, so a second tap cannot open a second one.</summary>
    private bool _choosing;

    public ChoiceField()
    {
        InitializeComponent();
        Paint();
    }

    /// <summary>The labels to choose from. Swapping the list clears the selection.</summary>
    public IList<string>? ItemsSource
    {
        get => (IList<string>?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    /// <summary>The chosen row, or -1 for none. Held inside <see cref="ItemsSource"/>.</summary>
    public int SelectedIndex
    {
        get => (int)GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    /// <summary>The chosen label, or null for none.</summary>
    public string? SelectedItem
    {
        get => (string?)GetValue(SelectedItemProperty);
        set => SetValue(SelectedItemProperty, value);
    }

    /// <summary>Shown in the field while nothing is chosen — the Picker's placeholder, under the Picker's name.</summary>
    public string? Title
    {
        get => (string?)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>
    /// The heading of the sheet. Falls back to <see cref="Title"/>; a field whose placeholder is a
    /// value ("Not recorded") rather than a question sets this to the question.
    /// </summary>
    public string? Prompt
    {
        get => (string?)GetValue(PromptProperty);
        set => SetValue(PromptProperty, value);
    }

    /// <summary>Raised when the chosen row moved — by a tap in the sheet or by the page — and only then.</summary>
    public event EventHandler? SelectedIndexChanged;

    protected override void OnPropertyChanged(string? propertyName = null)
    {
        base.OnPropertyChanged(propertyName);

        if (propertyName == IsEnabledProperty.PropertyName)
            Field.Opacity = IsEnabled ? 1 : DisabledOpacity;
        else if (propertyName == SemanticProperties.HintProperty.PropertyName)
            // The pages set the hint on the field, as they did on the Picker; the Border is what
            // a screen reader lands on, so it is passed down.
            SemanticProperties.SetHint(Field, SemanticProperties.GetHint(this));
        else if (propertyName == SemanticProperties.DescriptionProperty.PropertyName)
            Paint();
    }

    private void OnOptionsChanged(IList<string>? options) =>
        Settle(_selection.SetOptions(options is null ? null : [.. options]));

    private void OnIndexRequested(int index)
    {
        if (!_mirroring)
            Settle(_selection.Select(index));
    }

    private void OnItemRequested(string? item)
    {
        if (!_mirroring)
            Settle(_selection.Select(item));
    }

    /// <summary>
    /// Brings the bindable properties in line with the selection — which may have held a request
    /// back inside the list — repaints, and announces the change if there was one.
    /// </summary>
    private void Settle(bool moved)
    {
        _mirroring = true;
        try
        {
            SelectedIndex = _selection.SelectedIndex;
            SelectedItem = _selection.SelectedItem;
        }
        finally
        {
            _mirroring = false;
        }

        Paint();

        if (moved)
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
    }

    private async void OnTapped(object? sender, TappedEventArgs e)
    {
        if (!IsEnabled || _choosing || _selection.Options.Count == 0)
            return;

        _choosing = true;
        try
        {
            var popups = ServiceHelper.GetRequiredService<IPopupService>();
            var picked = await popups.ChooseIndexAsync(
                Prompt ?? Title ?? string.Empty, _selection.Options, _selection.SelectedIndex);

            if (picked is { } index)
                SelectedIndex = index;
        }
        finally
        {
            _choosing = false;
        }
    }

    private void Paint()
    {
        var chosen = _selection.SelectedItem;
        ValueLabel.Text = chosen ?? Title ?? string.Empty;
        // The placeholder takes the ink the Entry fields give theirs, so an unset choice reads as
        // an empty field rather than as a value.
        ValueLabel.TextColor = MetricStatus.Resource(chosen is null ? "BodyText" : "HeadingText", Colors.Gray);

        // The page's description is the label when it set one; the field's own prompt otherwise.
        // Said the same way the date field says it — see FieldDescription.
        SemanticProperties.SetDescription(
            Field,
            FieldDescription.For(SemanticProperties.GetDescription(this) ?? Prompt ?? Title, chosen, "not set"));
    }
}
