using System.ComponentModel;
using CardiTrack.Mobile.Core.Charts;
// CardiTrack.Application shadows MAUI's Application in any file importing it — see NudgeMiniRow.
using MauiApplication = Microsoft.Maui.Controls.Application;

namespace CardiTrack.Mobile.Controls;

/// <summary>
/// One card of the Member Detail screen's Key Metric Trends carousel (issue #188): the metric's
/// icon, name, latest reading and status, over a chart of the caregiver's chosen window. Bound to a
/// <see cref="MetricTrend"/> and built in code, like the row it replaced.
/// </summary>
/// <remarks>
/// The chart is full card width and takes every dp the rest of the card leaves it — at least
/// <see cref="MinimumChartHeight"/>, and rather more on most metrics — against the 64×24 sparkline
/// the old row had room for. The point of giving each metric its own swipeable card is that the
/// shape of the trend is readable, not just present.
/// </remarks>
public sealed class MetricTrendCard : ContentView
{
    /// <summary>
    /// The least plot a card will show. The chart does not have a fixed height — it takes whatever
    /// its card has left over (see <see cref="CardHeight"/>) — and this is the floor that stops a
    /// metric with a long footer, both legend entries and a large system font from squeezing the
    /// line down to a strip.
    /// </summary>
    private const double MinimumChartHeight = 96;

    /// <summary>
    /// The row the carousel reserves for every card: padding, header, chart, date rule, legend,
    /// bottom explanation strip and margin.
    /// </summary>
    /// <remarks>
    /// One height for all six metrics, because a carousel whose slides changed height would jolt
    /// the page on every swipe. The cards do not all need the same room, though — a metric with no
    /// legend entries and a one-line footer is the better part of 50dp shorter than heart rate's —
    /// so rather than every card being padded out to the tallest one and the short ones ending in
    /// dead white space, the chart row is the one that flexes: it absorbs whatever its own card
    /// leaves over. Activity's plot is therefore taller than heart rate's, and both are fully
    /// used.
    /// <para>
    /// This figure is set so the tallest card still clears <see cref="MinimumChartHeight"/>. It is
    /// derived rather than measured — worth a look on a device, and on one running a large system
    /// font, if the carousel ever looks tight.
    /// </para>
    /// </remarks>
    public const double CardHeight = 330;

    /// <summary>Beyond a fortnight, a marker per day crowds the line rather than reading as data.</summary>
    private const int MarkerWindowLimit = 14;

    /// <summary>
    /// The annotation type scale: axis numbers, dates and legend keys. Was 11 — legible to whoever
    /// laid the card out, and not to a caregiver who is the reason this product has a large-text
    /// story at all.
    /// </summary>
    private const double AnnotationFontSize = 12;

    /// <summary>
    /// The strip under the chart. A point above the annotations around it: this is the one line on
    /// the card that has to be read as a sentence rather than glanced at as a label.
    /// </summary>
    private const double FooterFontSize = 13;

    private readonly Image _icon = new() { WidthRequest = 22, HeightRequest = 22 };
    private readonly Label _name = new();
    private readonly Label _windowCaption = new();
    private readonly Label _value = new();
    private readonly Border _pill;
    private readonly Label _pillText = new();
    private readonly TrendChart _chart = new();
    private readonly Label _max = new();
    private readonly Label _min = new();
    private readonly Label _startDate = new();
    private readonly Label _endDate = new();
    private readonly Label _empty = new();
    private readonly Grid _plot;
    private readonly Grid _dates;
    private readonly Label _baselineKey = new();
    private readonly Label _referenceKey = new();
    private readonly TrendLegendSwatch _baselineSwatch = new(TrendLegendMark.Baseline);
    private readonly HorizontalStackLayout _baselineLegend;
    private readonly HorizontalStackLayout _referenceLegend;
    private readonly Grid _legend;
    private readonly Label _footer = new();

    /// <summary>The "i" itself — the thing the hint pulse scales. Assigned by BuildFooter.</summary>
    private Border _hintDisc = null!;

    /// <summary>
    /// Large enough to be seen as a control rather than a full stop, at the 44dp effective target
    /// its padded wrapper already gave it.
    /// </summary>
    private const double HintDiscSize = 30;

    private MetricTrend? _trend;

    /// <summary>The panel behind the "i", rebuilt whenever the card is bound to a metric.</summary>
    private string _explanation = string.Empty;

    public MetricTrendCard()
    {
        ApplyStyle(_name, "Body1SemiBoldDark");
        ApplyStyle(_windowCaption, "Body2");
        _windowCaption.FontSize = 12;
        _windowCaption.TextColor = MetricStatus.Resource("MutedText", Colors.Gray);
        ApplyStyle(_value, "Heading3");
        _value.HorizontalTextAlignment = TextAlignment.End;
        ApplyStyle(_pillText, "StatusPillText");

        _pill = new Border
        {
            IsVisible = false,
            Style = Resource<Style>("StatusPill"),
            HorizontalOptions = LayoutOptions.End,
            Content = _pillText,
        };

        var iconTile = new Border
        {
            Style = Resource<Style>("IconTile"),
            BackgroundColor = MetricStatus.Resource("MetricTileTint", Colors.LightGray),
            WidthRequest = 40,
            HeightRequest = 40,
            VerticalOptions = LayoutOptions.Start,
            Content = _icon,
        };

        var title = new VerticalStackLayout { Spacing = 1, VerticalOptions = LayoutOptions.Center };
        title.Add(_name);
        title.Add(_windowCaption);

        var reading = new VerticalStackLayout { Spacing = 4, VerticalOptions = LayoutOptions.Center };
        reading.Add(_value);
        reading.Add(_pill);

        var header = new Grid
        {
            ColumnDefinitions =
            [
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
            ],
            ColumnSpacing = 10,
        };
        header.Add(iconTile);
        header.Add(title, 1);
        header.Add(reading, 2);

        // Min and max sit beside the chart rather than on it: the line needs the full plot height,
        // and a label floating over it would collide with the very peak it names.
        foreach (var axisLabel in new[] { _max, _min })
        {
            ApplyStyle(axisLabel, "Body2");
            axisLabel.FontSize = AnnotationFontSize;
            axisLabel.TextColor = MetricStatus.Resource("MutedText", Colors.Gray);
            axisLabel.HorizontalTextAlignment = TextAlignment.End;
        }
        _max.VerticalOptions = LayoutOptions.Start;
        _min.VerticalOptions = LayoutOptions.End;

        var axis = new Grid
        {
            RowDefinitions = [new RowDefinition(GridLength.Star), new RowDefinition(GridLength.Star)],
        };
        axis.Add(_max);
        axis.Add(_min, 0, 1);

        // No height of its own: the plot fills the one flexible row of the card — see CardHeight.
        _plot = new Grid
        {
            ColumnDefinitions = [new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star)],
            ColumnSpacing = 8,
            MinimumHeightRequest = MinimumChartHeight,
        };
        _plot.Add(axis);
        _plot.Add(_chart, 1);

        ApplyStyle(_empty, "Body2");
        _empty.Text = "Not enough readings in this window yet.";
        _empty.HorizontalTextAlignment = TextAlignment.Center;
        _empty.VerticalOptions = LayoutOptions.Center;
        _empty.IsVisible = false;

        foreach (var dateLabel in new[] { _startDate, _endDate })
        {
            ApplyStyle(dateLabel, "Body2");
            dateLabel.FontSize = AnnotationFontSize;
            dateLabel.TextColor = MetricStatus.Resource("MutedText", Colors.Gray);
        }
        _endDate.HorizontalTextAlignment = TextAlignment.End;

        _dates = new Grid
        {
            ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star)],
        };
        _dates.Add(_startDate);
        _dates.Add(_endDate, 1);

        // The chart draws two things that are not readings — this member's baseline and the
        // published range — and neither carries its own label on a plot this size, so the key
        // names them and quotes the numbers behind them.
        _baselineLegend = BuildLegendEntry(_baselineSwatch, _baselineKey);
        _referenceLegend = BuildLegendEntry(new TrendLegendSwatch(TrendLegendMark.Reference), _referenceKey);

        _legend = new Grid
        {
            ColumnDefinitions = [new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star)],
            ColumnSpacing = 14,
        };
        _legend.Add(_baselineLegend);
        _legend.Add(_referenceLegend, 1);

        // The key names the two marks; this says what they mean. Naming is not explaining — that a
        // member's own normal matters more than the published one, or that a wrist wearable is not
        // taking anyone's temperature, is the part a non-clinical reader actually needs.
        var footerBlock = BuildFooter();

        // The chart's row is the only Star one: everything else on the card is as tall as its own
        // content, and the plot takes the rest. See CardHeight.
        var body = new Grid
        {
            RowDefinitions =
            [
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Star),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
            ],
            RowSpacing = 12,
        };
        body.Add(header);
        body.Add(_plot, 0, 1);
        body.Add(_empty, 0, 1);
        body.Add(_dates, 0, 2);
        body.Add(_legend, 0, 3);
        body.Add(footerBlock, 0, 4);

        Content = new Border
        {
            Style = Resource<Style>("ElevatedCard"),
            // Room for the card's own shadow inside the carousel's viewport, which clips.
            Margin = new Thickness(3, 3, 3, 10),
            Content = body,
        };

        Loaded += (_, _) => StartHintPulse();
        // Same cleanup as SkeletonView: an animation left running on a torn-down card holds a
        // reference to it and keeps ticking against nothing.
        Unloaded += (_, _) => StopHintPulse();
    }

    protected override void OnBindingContextChanged()
    {
        base.OnBindingContextChanged();

        // The carousel recycles cards across items, so the previous item must be let go of or it
        // would keep redrawing a card that no longer shows it.
        if (_trend is not null)
            _trend.PropertyChanged -= OnTrendChanged;

        _trend = BindingContext as MetricTrend;

        if (_trend is not null)
            _trend.PropertyChanged += OnTrendChanged;

        Render();
    }

    private void OnTrendChanged(object? sender, PropertyChangedEventArgs e) => Render();

    private void Render()
    {
        if (_trend is null)
            return;

        _icon.Source = _trend.IconSource;
        _name.Text = _trend.Name;
        _windowCaption.Text = $"Last {_trend.Days} days";
        _value.Text = _trend.ValueText;
        SemanticProperties.SetDescription(this, $"{_trend.Name}, {_trend.ValueText}, last {_trend.Days} days");

        if (MetricStatus.Pill(_trend.Metric.Status) is { } pill)
        {
            _pill.BackgroundColor = MetricStatus.Resource(pill.Tint, Colors.Transparent);
            _pillText.TextColor = MetricStatus.Resource(pill.Ink, Colors.Gray);
            _pillText.Text = pill.Text;
            _pill.IsVisible = true;
        }
        else
        {
            _pill.IsVisible = false;
        }

        var points = _trend.Window;
        var values = points.Where(p => p.Value is not null).Select(p => p.Value!.Value).ToList();

        // One point draws no line, so the card says so rather than showing an empty grid the
        // caregiver would have to interpret.
        var hasChart = values.Count >= 2;
        _plot.IsVisible = hasChart;
        _dates.IsVisible = hasChart;
        _legend.IsVisible = hasChart;
        _empty.IsVisible = !hasChart;
        if (!hasChart)
            return;

        var baseline = _trend.Metric.Baseline;
        var reference = _trend.Metric.Reference;

        // The metric's own ink, not its status accent: the line says which metric this is, and the
        // pill above it says how the member is doing. A line that changed colour with severity
        // would make the reader re-identify the card every time the reading moved between bands.
        // Resolved once — the chart, its baseline rule and the key that names that rule all wear
        // it, and looking it up three times is three chances for them to disagree.
        var ink = MetricStatus.Resource(_trend.InkKey, MetricStatus.Accent(_trend.Metric.Status));
        _baselineSwatch.Ink = ink;

        // The axis labels name the extent the chart actually plots over, which the baseline and
        // the reference band get a say in — so both are read off the one scale rather than the
        // labels quoting the readings while the line is drawn against something wider.
        var scale = TrendScale.For(
            (double)values.Min(),
            (double)values.Max(),
            baseline is { } b ? (double)b : null,
            reference is not null ? (double)reference.Low : null,
            reference is not null ? (double)reference.High : null);

        _max.Text = string.Format(_trend.AxisFormat, (decimal)scale.Max);
        _min.Text = string.Format(_trend.AxisFormat, (decimal)scale.Min);
        _startDate.Text = points[0].Date.ToString("MMM d");
        _endDate.Text = points[^1].Date.ToString("MMM d");

        // A baseline the scale could not make room for is not drawn — see TrendScale.For — but its
        // number is the thing most worth knowing in exactly that case: a window sitting that far
        // from the member's own normal is *why* the rule would not fit. So the key keeps it and
        // drops the dash that would otherwise point at a rule the caregiver cannot find, saying
        // plainly where it went.
        var baselineDrawn = baseline is { } shown && scale.Contains((double)shown);
        _baselineLegend.IsVisible = _trend.BaselineText is not null;
        _baselineSwatch.IsVisible = baselineDrawn;
        _baselineKey.Text = _trend.BaselineText is { } baselineText
            ? (baselineDrawn ? baselineText : $"{baselineText} (off chart)")
            : string.Empty;

        _referenceLegend.IsVisible = _trend.ReferenceText is not null;
        _referenceKey.Text = _trend.ReferenceText ?? string.Empty;

        // A hidden entry leaves its column empty rather than absent, and the gap either side of it
        // would read as an indent on a key that starts with its swatch.
        _legend.ColumnSpacing = _baselineLegend.IsVisible && _referenceLegend.IsVisible ? 14 : 0;

        var (footer, explanation) = MetricExplanations.For(_trend.Name, _trend.Metric, _trend.AxisFormat);
        _footer.Text = footer;
        _explanation = explanation;

        // The chart itself is a canvas with nothing for a screen reader to walk, so what it plots
        // besides the readings has to reach one through the card's own description.
        var comparisons = string.Join(", ", new[] { _trend.BaselineText, _trend.ReferenceText }.Where(t => t is not null));
        if (comparisons.Length > 0)
            SemanticProperties.SetDescription(
                this, $"{_trend.Name}, {_trend.ValueText}, last {_trend.Days} days. {comparisons}");

        _chart.Render(
            points,
            scale,
            ink,
            showMarkers: points.Count <= MarkerWindowLimit,
            baseline,
            reference);
    }

    /// <summary>
    /// A beat of the "i" hint: enough to catch the eye at the edge of vision, not enough to be a
    /// thing happening on the screen.
    /// </summary>
    private const double PulseScale = 1.12;

    private const uint PulseCycleMs = 1400;

    /// <summary>Two beats and it rests. A hint that keeps going is not a hint.</summary>
    private const int PulseCycles = 2;

    private const string PulseAnimation = "explanationHint";

    private const string ExplanationOpenedKey = "trend.explanationOpened";

    /// <summary>
    /// Whether this caregiver has ever opened an explanation, on any card. Once they have, the
    /// hint has done its job and no card pulses again — this screen is opened by people who are
    /// worried about someone, and a permanent animation in the corner of it costs more than the
    /// affordance it advertises.
    /// </summary>
    /// <remarks>
    /// Latched in a static so the six cards the carousel realises cost one preference read
    /// between them rather than one each.
    /// </remarks>
    private static bool? _explanationOpened;

    private static bool ExplanationOpened
    {
        get => _explanationOpened ??= Preferences.Default.Get(ExplanationOpenedKey, false);
        set
        {
            _explanationOpened = value;
            Preferences.Default.Set(ExplanationOpenedKey, value);
        }
    }

    /// <summary>
    /// Starts the hint, unless this caregiver has already found the "i" for themselves.
    /// </summary>
    /// <remarks>
    /// MAUI surfaces no cross-platform reduce-motion setting, so this cannot honour one without
    /// per-platform code. The mitigations are the ones available here: it is small, it is brief,
    /// it stops after two beats, and it never returns once the panel behind it has been opened.
    /// </remarks>
    private void StartHintPulse()
    {
        if (ExplanationOpened || _trend is null)
            return;

        var beats = 0;
        var pulse = new Animation
        {
            { 0.0, 0.35, new Animation(v => _hintDisc.Scale = v, 1.0, PulseScale, Easing.CubicInOut) },
            { 0.35, 0.7, new Animation(v => _hintDisc.Scale = v, PulseScale, 1.0, Easing.CubicInOut) },
            // 0.7–1.0 is deliberately empty. The rest between beats is what makes this read as a
            // pulse rather than a throb.
        };

        pulse.Commit(this, PulseAnimation, length: PulseCycleMs, repeat: () => ++beats < PulseCycles);
    }

    /// <summary>
    /// Stops the hint and puts the disc back where it started — the carousel recycles cards, and
    /// one torn off mid-beat would otherwise be reused at whatever scale it had reached.
    /// </summary>
    private void StopHintPulse()
    {
        this.AbortAnimation(PulseAnimation);
        _hintDisc.Scale = 1;
    }

    /// <summary>
    /// The card's bottom strip: a hairline, the explanation, and the "i" that opens the rest of it.
    /// </summary>
    private Grid BuildFooter()
    {
        ApplyStyle(_footer, "Body2");
        _footer.FontSize = FooterFontSize;
        _footer.TextColor = MetricStatus.Resource("MutedText", Colors.Gray);
        _footer.LineBreakMode = LineBreakMode.WordWrap;
        _footer.VerticalOptions = LayoutOptions.Center;
        // Justified: this is the one block of prose on the card, and it sits under a chart whose
        // plot area is a hard rectangle — a ragged edge beside that reads as a misaligned column.
        _footer.HorizontalTextAlignment = TextAlignment.Justify;

        _hintDisc = new Border
        {
            WidthRequest = HintDiscSize,
            HeightRequest = HintDiscSize,
            StrokeThickness = 0,
            BackgroundColor = MetricStatus.Resource("SelectedOptionBackground", Colors.LightGray),
            // Fully qualified: Microsoft.Maui.Controls.Shapes is not among the SDK's implicit
            // usings, and importing it here would put its Path beside System.IO's.
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = HintDiscSize / 2 },
            Content = new Label
            {
                Text = "i",
                FontFamily = "QuicksandSemiBold",
                FontSize = 16,
                TextColor = MetricStatus.Resource("Primary", Colors.Blue),
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
            },
        };

        // The gesture sits on the padded wrapper, not the disc itself: hit-testing follows visual
        // bounds, so the padding is what makes this a target an older thumb can actually land on.
        var button = new Grid
        {
            Padding = new Thickness(10, 6, 0, 6),
            VerticalOptions = LayoutOptions.Center,
            Children = { _hintDisc },
        };
        SemanticProperties.SetDescription(button, "What does this mean?");
        SemanticProperties.SetHint(button, "Explains what this reading is compared against");
        button.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(ShowExplanation) });

        var row = new Grid
        {
            ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)],
            ColumnSpacing = 6,
        };
        row.Add(_footer);
        row.Add(button, 1);

        var block = new Grid
        {
            RowDefinitions = [new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto)],
            RowSpacing = 8,
        };
        block.Add(new BoxView { Style = Resource<Style>("DividerLine") });
        block.Add(row, 0, 1);
        return block;
    }

    /// <summary>
    /// Opens the fuller explanation. This card is realised by a <c>DataTemplate</c> inside the
    /// carousel, so there is no page to hand an event to the way the hand-built cards do — it
    /// resolves the popup service the same way <see cref="Services.ResumeRefresh"/> resolves its
    /// notifier.
    /// </summary>
    private async void ShowExplanation()
    {
        if (_trend is null || string.IsNullOrEmpty(_explanation))
            return;

        // Found without being told, or found because the pulse said to — either way the hint is
        // spent, on this card and every other.
        ExplanationOpened = true;
        StopHintPulse();

        try
        {
            await Services.ServiceHelper.GetRequiredService<Services.IPopupService>()
                .ShowInfoAsync(_explanation, _trend.Name);
        }
        catch (Exception)
        {
            // async void, reached from a gesture: an explanation that will not open is not worth
            // taking the app down for.
        }
    }

    private static HorizontalStackLayout BuildLegendEntry(TrendLegendSwatch swatch, Label key)
    {
        ApplyStyle(key, "Body2");
        key.FontSize = AnnotationFontSize;
        key.TextColor = MetricStatus.Resource("MutedText", Colors.Gray);
        key.VerticalTextAlignment = TextAlignment.Center;
        key.LineBreakMode = LineBreakMode.TailTruncation;

        var entry = new HorizontalStackLayout { Spacing = 6, IsVisible = false };
        entry.Add(swatch);
        entry.Add(key);
        return entry;
    }

    private static T Resource<T>(string key) =>
        (T)MauiApplication.Current!.Resources[key];

    private static void ApplyStyle(Label label, string key)
    {
        if (MauiApplication.Current?.Resources.TryGetValue(key, out var value) == true && value is Style style)
            label.Style = style;
    }
}
