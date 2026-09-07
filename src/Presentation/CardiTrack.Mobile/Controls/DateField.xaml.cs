using System.Globalization;
using CardiTrack.Mobile.Core.Forms;

namespace CardiTrack.Mobile.Controls;

/// <summary>
/// A day, drawn as one of the app's own fields and chosen in the platform's own dialog. Stands in
/// for the platform <see cref="DatePicker"/>, whose surface it keeps — <see cref="Date"/>,
/// <see cref="MinimumDate"/>, <see cref="MaximumDate"/>, <see cref="Format"/>,
/// <see cref="DateSelected"/> — so a page swaps one for the other without touching its handlers.
/// </summary>
/// <remarks>
/// <para>
/// What looked stock about the DatePicker was its own chrome: a system underline and system ink
/// in a form of rounded white fields. The dialog behind it is not the problem — it is the calendar
/// each platform's users already know, it speaks to screen readers, and it is the same on every
/// page that asks for a date. So the picker stays, sized to nothing and drawn at nothing, and the
/// field's tap is passed on to it — as a click on Android, as focus on iOS, which is what opens
/// the dialog on each.
/// </para>
/// <para>
/// The clamping lives in <see cref="DateBounds"/>, where it is tested: the date is held inside
/// the bounds however it is set, and only the day is kept.
/// </para>
/// </remarks>
public partial class DateField : ContentView
{
    private const double DisabledOpacity = 0.5;

    public static readonly BindableProperty DateProperty = BindableProperty.Create(
        nameof(Date),
        typeof(DateTime?),
        typeof(DateField),
        null,
        BindingMode.TwoWay,
        coerceValue: (bindable, value) =>
        {
            var field = (DateField)bindable;
            return DateBounds.Clamp((DateTime?)value, field.MinimumDate, field.MaximumDate);
        },
        propertyChanged: (bindable, oldValue, newValue) =>
            ((DateField)bindable).OnDateChanged((DateTime?)oldValue, (DateTime?)newValue));

    public static readonly BindableProperty MinimumDateProperty = BindableProperty.Create(
        nameof(MinimumDate),
        typeof(DateTime),
        typeof(DateField),
        DateBounds.DefaultMinimum,
        propertyChanged: (bindable, _, _) => ((DateField)bindable).OnBoundsChanged());

    public static readonly BindableProperty MaximumDateProperty = BindableProperty.Create(
        nameof(MaximumDate),
        typeof(DateTime),
        typeof(DateField),
        DateBounds.DefaultMaximum,
        propertyChanged: (bindable, _, _) => ((DateField)bindable).OnBoundsChanged());

    public static readonly BindableProperty FormatProperty = BindableProperty.Create(
        nameof(Format),
        typeof(string),
        typeof(DateField),
        "d MMM yyyy",
        propertyChanged: (bindable, _, _) => ((DateField)bindable).Paint());

    public static readonly BindableProperty PlaceholderProperty = BindableProperty.Create(
        nameof(Placeholder),
        typeof(string),
        typeof(DateField),
        "Choose a date",
        propertyChanged: (bindable, _, _) => ((DateField)bindable).Paint());

    public static readonly BindableProperty HasErrorProperty = BindableProperty.Create(
        nameof(HasError),
        typeof(bool),
        typeof(DateField),
        false,
        propertyChanged: (bindable, _, _) => ((DateField)bindable).Paint());

    /// <summary>Set while the platform picker is being brought in line with <see cref="Date"/>.</summary>
    private bool _mirroring;

    public DateField()
    {
        InitializeComponent();
        Paint();
    }

    /// <summary>The chosen day, or null for none. Held inside <see cref="MinimumDate"/>..<see cref="MaximumDate"/>.</summary>
    public DateTime? Date
    {
        get => (DateTime?)GetValue(DateProperty);
        set => SetValue(DateProperty, value);
    }

    /// <summary>The earliest day the dialog offers.</summary>
    public DateTime MinimumDate
    {
        get => (DateTime)GetValue(MinimumDateProperty);
        set => SetValue(MinimumDateProperty, value);
    }

    /// <summary>The latest day the dialog offers.</summary>
    public DateTime MaximumDate
    {
        get => (DateTime)GetValue(MaximumDateProperty);
        set => SetValue(MaximumDateProperty, value);
    }

    /// <summary>How the day is written in the field; a <see cref="DateTime.ToString(string)"/> format.</summary>
    public string Format
    {
        get => (string)GetValue(FormatProperty);
        set => SetValue(FormatProperty, value);
    }

    /// <summary>Shown in the field while no day is chosen.</summary>
    public string Placeholder
    {
        get => (string)GetValue(PlaceholderProperty);
        set => SetValue(PlaceholderProperty, value);
    }

    /// <summary>
    /// Whether the page has refused the day in the field. Draws the edge in ErrorRed, the same
    /// mark the sign-up form and the alarm builder put on a refused Entry; the message itself is
    /// the page's, under the field.
    /// </summary>
    public bool HasError
    {
        get => (bool)GetValue(HasErrorProperty);
        set => SetValue(HasErrorProperty, value);
    }

    /// <summary>Old and new day, in the shape the platform picker raises.</summary>
    public event EventHandler<DateChangedEventArgs>? DateSelected;

    protected override void OnPropertyChanged(string? propertyName = null)
    {
        base.OnPropertyChanged(propertyName);

        if (propertyName == IsEnabledProperty.PropertyName)
            Field.Opacity = IsEnabled ? 1 : DisabledOpacity;
        else if (propertyName == SemanticProperties.HintProperty.PropertyName)
            SemanticProperties.SetHint(Field, SemanticProperties.GetHint(this));
        else if (propertyName == SemanticProperties.DescriptionProperty.PropertyName)
            // The page's description is the field's label for a screen reader; the value is
            // appended to it, so it is repainted rather than passed down as it is.
            Paint();
    }

    private void OnTapped(object? sender, TappedEventArgs e)
    {
        if (!IsEnabled)
            return;

#if ANDROID
        // Android's picker is an EditText that opens its dialog from its click listener, and it
        // refuses focus in touch mode — so Focus() lands nowhere, and the tap is handed on as the
        // click it would have been had the caregiver touched the picker itself. Same outcome as
        // iOS below: the platform's own dialog, opened by a tap on the field.
        if (Picker.Handler?.PlatformView is Android.Views.View view)
        {
            view.PerformClick();
            return;
        }
#endif

        // On iOS the picker is a text field whose input view is the calendar; first responder is
        // what opens it.
        Picker.Focus();
    }

    private void OnPickerDateSelected(object? sender, DateChangedEventArgs e)
    {
        if (!_mirroring)
            Date = e.NewDate;
    }

    private void OnBoundsChanged()
    {
        // The platform picker refuses an earliest above its latest, so the two are handed over in
        // the order that keeps them consistent at every step.
        //
        // The pair is read from this control's own properties first, and normalised — a page that
        // sets the two one after the other can leave them upside down for one statement, and
        // handing an upside-down pair to the picker in either order throws. Only then is the
        // order decided, against what the picker holds now.
        var (earliest, latest) = DateBounds.Normalise(MinimumDate, MaximumDate);

        _mirroring = true;
        try
        {
            // A picker holding no latest has nothing the new earliest can be past.
            if (DateBounds.LatestFirst(earliest, Picker.MaximumDate ?? DateTime.MaxValue))
            {
                Picker.MaximumDate = latest;
                Picker.MinimumDate = earliest;
            }
            else
            {
                Picker.MinimumDate = earliest;
                Picker.MaximumDate = latest;
            }
        }
        finally
        {
            _mirroring = false;
        }

        // A bound that moved past the day drags the day with it, which re-enters OnDateChanged.
        CoerceValue(DateProperty);
    }

    private void OnDateChanged(DateTime? oldValue, DateTime? newValue)
    {
        _mirroring = true;
        try
        {
            if (newValue is { } day && Picker.Date != day)
                Picker.Date = day;
        }
        finally
        {
            _mirroring = false;
        }

        Paint();
        DateSelected?.Invoke(this, new DateChangedEventArgs(oldValue, newValue));
    }

    private void Paint()
    {
        var day = Date;
        var text = day is { } d ? d.ToString(Format, CultureInfo.CurrentCulture) : Placeholder;
        ValueLabel.Text = text;
        // The placeholder takes the ink the Entry fields give theirs, so an unset day reads as an
        // empty field rather than as a value.
        ValueLabel.TextColor = MetricStatus.Resource(day is null ? "BodyText" : "HeadingText", Colors.Gray);
        Field.Stroke = new SolidColorBrush(MetricStatus.Resource(HasError ? "ErrorRed" : "InputBorder", Colors.Gray));

        // The Border is the one thing a screen reader lands on — the platform picker behind it
        // is out of the tree — so it carries the page's label and the day together.
        SemanticProperties.SetDescription(
            Field,
            FieldDescription.For(SemanticProperties.GetDescription(this), day is null ? null : text, "no date chosen"));
    }
}
