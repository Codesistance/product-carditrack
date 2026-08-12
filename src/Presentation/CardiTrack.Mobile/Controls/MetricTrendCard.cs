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
/// The chart is <see cref="ChartHeight"/> tall and full card width, against the 64×24 sparkline the
/// old row had room for — the point of giving each metric its own swipeable card is that the shape
/// of the trend is readable, not just present.
/// </remarks>
public sealed class MetricTrendCard : ContentView
{
    /// <summary>Three times the 24dp sparkline this card's predecessor drew.</summary>
    private const double ChartHeight = 72;

    /// <summary>
    /// The row the carousel reserves for a card: its padding, header, chart, date rule, legend and
    /// margin, plus slack for a caregiver running a larger system font. The carousel clips, so this
    /// is a floor rather than a fit.
    /// </summary>
    public const double CardHeight = 240;

    /// <summary>Beyond a fortnight, a marker per day crowds the line rather than reading as data.</summary>
    private const int MarkerWindowLimit = 14;

    private readonly Image _icon = new() { WidthRequest = 22, HeightRequest = 22 };
    private readonly Label _name = new();
    private readonly Label _windowCaption = new();
    private readonly Label _value = new();
    private readonly Border _pill;
    private readonly Label _pillText = new();
    private readonly TrendChart _chart = new() { HeightRequest = ChartHeight };
    private readonly Label _max = new();
    private readonly Label _min = new();
    private readonly Label _startDate = new();
    private readonly Label _endDate = new();
    private readonly Label _empty = new();
    private readonly Grid _plot;
    private readonly Grid _dates;
    private readonly Label _baselineKey = new();
    private readonly Label _referenceKey = new();
    private readonly HorizontalStackLayout _baselineLegend;
    private readonly HorizontalStackLayout _referenceLegend;
    private readonly Grid _legend;

    private MetricTrend? _trend;

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
            axisLabel.FontSize = 11;
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

        _plot = new Grid
        {
            ColumnDefinitions = [new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star)],
            ColumnSpacing = 8,
            HeightRequest = ChartHeight,
        };
        _plot.Add(axis);
        _plot.Add(_chart, 1);

        ApplyStyle(_empty, "Body2");
        _empty.Text = "Not enough readings in this window yet.";
        _empty.HorizontalTextAlignment = TextAlignment.Center;
        _empty.VerticalOptions = LayoutOptions.Center;
        _empty.IsVisible = false;
        _empty.HeightRequest = ChartHeight;

        foreach (var dateLabel in new[] { _startDate, _endDate })
        {
            ApplyStyle(dateLabel, "Body2");
            dateLabel.FontSize = 11;
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
        _baselineLegend = BuildLegendEntry(TrendLegendMark.Baseline, _baselineKey);
        _referenceLegend = BuildLegendEntry(TrendLegendMark.Reference, _referenceKey);

        _legend = new Grid
        {
            ColumnDefinitions = [new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star)],
            ColumnSpacing = 14,
        };
        _legend.Add(_baselineLegend);
        _legend.Add(_referenceLegend, 1);

        var body = new Grid
        {
            RowDefinitions =
            [
                new RowDefinition(GridLength.Auto),
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

        Content = new Border
        {
            Style = Resource<Style>("ElevatedCard"),
            // Room for the card's own shadow inside the carousel's viewport, which clips.
            Margin = new Thickness(3, 3, 3, 10),
            Content = body,
        };
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

        // A baseline the scale could not make room for is not drawn — see TrendScale.For — so its
        // key goes with it rather than pointing at a line that isn't there.
        _baselineLegend.IsVisible = baseline is { } shown && scale.Contains((double)shown);
        _baselineKey.Text = _trend.BaselineText ?? string.Empty;

        _referenceLegend.IsVisible = _trend.ReferenceText is not null;
        _referenceKey.Text = _trend.ReferenceText ?? string.Empty;

        // A hidden entry leaves its column empty rather than absent, and the gap either side of it
        // would read as an indent on a key that starts with its swatch.
        _legend.ColumnSpacing = _baselineLegend.IsVisible && _referenceLegend.IsVisible ? 14 : 0;

        // The chart itself is a canvas with nothing for a screen reader to walk, so what it plots
        // besides the readings has to reach one through the card's own description.
        var comparisons = string.Join(", ", new[] { _trend.BaselineText, _trend.ReferenceText }.Where(t => t is not null));
        if (comparisons.Length > 0)
            SemanticProperties.SetDescription(
                this, $"{_trend.Name}, {_trend.ValueText}, last {_trend.Days} days. {comparisons}");

        _chart.Render(
            points,
            scale,
            MetricStatus.Accent(_trend.Metric.Status),
            showMarkers: points.Count <= MarkerWindowLimit,
            baseline,
            reference);
    }

    private static HorizontalStackLayout BuildLegendEntry(TrendLegendMark mark, Label key)
    {
        ApplyStyle(key, "Body2");
        key.FontSize = 11;
        key.TextColor = MetricStatus.Resource("MutedText", Colors.Gray);
        key.VerticalTextAlignment = TextAlignment.Center;
        key.LineBreakMode = LineBreakMode.TailTruncation;

        var entry = new HorizontalStackLayout { Spacing = 6, IsVisible = false };
        entry.Add(new TrendLegendSwatch(mark));
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
