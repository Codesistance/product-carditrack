using CardiTrack.Application.DTOs.Common;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Core.Charts;

namespace CardiTrack.Mobile.Controls;

/// <summary>
/// One supporting chart inside a member-chat reply bubble: the series' name over a small
/// <see cref="TrendChart"/>. This is the real rendering the member-chat plan deferred when the
/// reply carried only a "Steps: 3,201 → 5,110" text summary — drawn with the same primitive as
/// the Member Detail trends rather than the <c>DashboardMetric</c>-coupled card around it, which
/// chat's <see cref="ChartSeries"/> doesn't shape-match.
/// </summary>
public sealed class ChatSeriesChart : ContentView
{
    public static readonly BindableProperty ItemProperty = BindableProperty.Create(
        nameof(Item), typeof(ChatChartItem), typeof(ChatSeriesChart), propertyChanged: OnItemChanged);

    /// <summary>Annotation type: the axis figures and dates, a step down from the series title so
    /// they read as the chart's furniture rather than as more of its content.</summary>
    private const double AnnotationFontSize = 10;

    private readonly Label _title;
    private readonly TrendChart _chart;
    private readonly Label _max = new();
    private readonly Label _min = new();
    private readonly Label _startDate = new();
    private readonly Label _endDate = new();
    private readonly Label _baselineKey = new();
    private readonly Label _referenceKey = new();
    private readonly TrendLegendSwatch _baselineSwatch = new(TrendLegendMark.Baseline);
    private readonly HorizontalStackLayout _baselineLegend;
    private readonly HorizontalStackLayout _referenceLegend;
    private readonly Grid _legend;

    public ChatSeriesChart()
    {
        _title = new Label
        {
            FontFamily = "QuicksandSemiBold",
            FontSize = 11,
            TextColor = Microsoft.Maui.Controls.Application.Current?.Resources["BodyText"] as Color ?? Colors.Gray,
        };
        // Interactive: chat charts are the ones a caregiver reads mid-conversation, and a tap
        // answering "what was that day exactly?" beats asking the model a follow-up for one
        // number. Taller than the original 72 now that the bubble runs full width, and taller
        // again since the axis arrived — a week of readings squeezed into 96px put its high and
        // low labels almost on top of each other, and flattened the shape they were labelling.
        _chart = new TrendChart { HeightRequest = 120, Interactive = true };

        var muted = Microsoft.Maui.Controls.Application.Current?.Resources["MutedText"] as Color ?? Colors.Gray;
        foreach (var annotation in new[] { _max, _min, _startDate, _endDate })
        {
            annotation.FontSize = AnnotationFontSize;
            annotation.TextColor = muted;
        }

        // Beside the plot rather than on it: the line needs the full height, and a figure floating
        // over the chart would collide with the very peak it names. Same arrangement as
        // MetricTrendCard, which solved this once already.
        _max.HorizontalTextAlignment = TextAlignment.End;
        _min.HorizontalTextAlignment = TextAlignment.End;
        _max.VerticalOptions = LayoutOptions.Start;
        _min.VerticalOptions = LayoutOptions.End;

        var axis = new Grid
        {
            RowDefinitions = [new RowDefinition(GridLength.Star), new RowDefinition(GridLength.Star)],
        };
        axis.Add(_max);
        axis.Add(_min, 0, 1);

        // The dates line up with the ends of the line, not with the axis figures to its left, so
        // they share the plot's column rather than sitting in a grid of their own — one grid, so
        // the Auto column is measured once and both rows agree on where the plot starts.
        _endDate.HorizontalTextAlignment = TextAlignment.End;
        var dates = new Grid
        {
            ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star)],
        };
        dates.Add(_startDate);
        dates.Add(_endDate, 1);

        var plot = new Grid
        {
            ColumnDefinitions = [new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star)],
            RowDefinitions = [new RowDefinition(GridLength.Star), new RowDefinition(GridLength.Auto)],
            ColumnSpacing = 6,
        };
        plot.Add(axis);
        plot.Add(_chart, 1);
        plot.Add(dates, 1, 1);

        // The chart now draws the same two non-reading marks the Member Detail trends do — the
        // member's own usual and the published band — and neither carries its own label on a plot
        // this size, so the key names them and quotes the numbers behind them, exactly as
        // MetricTrendCard's legend does.
        foreach (var key in new[] { _baselineKey, _referenceKey })
        {
            key.FontSize = AnnotationFontSize;
            key.TextColor = muted;
            key.VerticalTextAlignment = TextAlignment.Center;
            key.LineBreakMode = LineBreakMode.TailTruncation;
        }
        _baselineLegend = BuildLegendEntry(_baselineSwatch, _baselineKey);
        _referenceLegend = BuildLegendEntry(new TrendLegendSwatch(TrendLegendMark.Reference), _referenceKey);
        _legend = new Grid
        {
            ColumnDefinitions = [new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star)],
            ColumnSpacing = 12,
        };
        _legend.Add(_baselineLegend);
        _legend.Add(_referenceLegend, 1);

        Content = new VerticalStackLayout
        {
            Spacing = 2,
            Children = { _title, plot, _legend },
        };
    }

    private static HorizontalStackLayout BuildLegendEntry(TrendLegendSwatch swatch, Label key)
    {
        var entry = new HorizontalStackLayout { Spacing = 6, IsVisible = false };
        entry.Add(swatch);
        entry.Add(key);
        return entry;
    }

    public ChatChartItem? Item
    {
        get => (ChatChartItem?)GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }

    private static void OnItemChanged(BindableObject bindable, object? oldValue, object? newValue)
    {
        var view = (ChatSeriesChart)bindable;
        if (newValue is not ChatChartItem item)
            return;

        view._title.Text = item.Title;
        view._chart.ValueFormatter = item.FormatValue;
        view._chart.Render(item.Points, item.Scale, item.Ink, showMarkers: true, item.Baseline, item.Reference);

        // Same rule as MetricTrendCard: a baseline the scale could not admit is not drawn, and the
        // key then carries its number with a plain note instead of a dash pointing at nothing.
        view._baselineSwatch.Ink = item.Ink;
        view._baselineSwatch.IsVisible = item.BaselineDrawn;
        view._baselineLegend.IsVisible = item.BaselineText is not null;
        view._baselineKey.Text = item.BaselineText ?? string.Empty;
        view._referenceLegend.IsVisible = item.ReferenceText is not null;
        view._referenceKey.Text = item.ReferenceText ?? string.Empty;
        view._legend.IsVisible = item.BaselineText is not null || item.ReferenceText is not null;
        view._legend.ColumnSpacing = item.BaselineText is not null && item.ReferenceText is not null ? 12 : 0;

        // The extent the chart actually plots over, not the readings' own min and max — those are
        // what TrendScale padded away from, and writing them here would put a number on the axis
        // that no gridline corresponds to. A flat week has no extent to label, so it gets neither
        // figure rather than the same number twice.
        view._max.Text = item.Scale.HasExtent ? item.AxisLabel(item.Scale.Max) : string.Empty;
        view._min.Text = item.Scale.HasExtent ? item.AxisLabel(item.Scale.Min) : string.Empty;

        view._startDate.Text = item.Points[0].Date.ToString("MMM d");
        view._endDate.Text = item.Points[^1].Date.ToString("MMM d");

        // The title label would otherwise be read on its own, announcing that a chart exists
        // without saying anything it shows. Same treatment MetricTrendCard gives its plot.
        SemanticProperties.SetDescription(view._chart, item.AccessibleSummary);
        SemanticProperties.SetHint(view._chart, "Tap a point to see that day's reading");
    }
}

/// <summary>
/// One reply series pre-shaped for drawing, following the same convention as <c>ChatTurnItem</c>
/// itself: every display decision is made at construction so the template stays converter-free
/// and <see cref="ChatSeriesChart"/> stays dumb.
/// </summary>
public sealed class ChatChartItem
{
    private ChatChartItem(
        string title,
        IReadOnlyList<MetricPoint> points,
        TrendScale scale,
        Color ink,
        string metric,
        decimal? baseline,
        MetricReference? reference)
    {
        Title = title;
        Points = points;
        Scale = scale;
        Ink = ink;
        Metric = metric;
        Baseline = baseline;
        Reference = reference;
    }

    public string Title { get; }
    public IReadOnlyList<MetricPoint> Points { get; }
    public TrendScale Scale { get; }
    public Color Ink { get; }

    /// <summary>This member's own usual for the metric, in the series' unit, as the reply carried
    /// it — drawn as the same dashed rule the Member Detail trends draw.</summary>
    public decimal? Baseline { get; }

    /// <summary>The published typical-adult band, already in the series' unit (the API converts
    /// sleep's hours to minutes), shaded behind the line as on the Member Detail trends.</summary>
    public MetricReference? Reference { get; }

    /// <summary>Whether the scale admitted the baseline — see <c>TrendScale.For</c>: a rule the
    /// plot cannot make room for is left to the legend, which then says where it went.</summary>
    public bool BaselineDrawn => Baseline is { } b && Scale.Contains((double)b);

    /// <summary>The legend entry for the member's own usual, or null when the reply carried none.
    /// "Their usual" rather than a per-metric noun: the series title directly above already names
    /// the metric, where the Detail card's legend has a whole card of context to lean on.</summary>
    public string? BaselineText =>
        Baseline is { } baseline
            ? BaselineDrawn
                ? $"Their usual: {AxisLabel((double)baseline)}"
                : $"Their usual: {AxisLabel((double)baseline)} (off chart)"
            : null;

    /// <summary>The legend entry for the published band, attributed — the same phrasing the
    /// Member Detail trends use, so the two surfaces name one range one way.</summary>
    public string? ReferenceText =>
        Reference is { } reference
            ? $"Typical {AxisLabel((double)reference.Low)}–{AxisLabel((double)reference.High)} ({reference.Source})"
            : null;

    /// <summary>The series name, kept for the unit the callout appends — the chart draws numbers,
    /// the series knows what they are.</summary>
    private string Metric { get; }

    /// <summary>
    /// A tapped reading as the callout shows it. Sleep arrives in minutes and is read in hours;
    /// steps are whole and want their thousands separator; a rate is a rate.
    /// </summary>
    public string FormatValue(double value) => ChatMetricFormat.WithUnit(Metric, value);

    /// <summary>
    /// The same number as <see cref="FormatValue"/> without the unit noun, for the two figures
    /// written beside the plot. The title a finger's width above already says "Steps", and
    /// repeating it on both ends of a 96-pixel axis costs more width than it explains. Sleep keeps
    /// its h/m shaping, because the stored unit is minutes and "465" beside a chart is a number no
    /// caregiver thinks in.
    /// </summary>
    public string AxisLabel(double value) => ChatMetricFormat.Bare(Metric, value);

    /// <summary>
    /// What the chart says to a caregiver who cannot see it. The canvas is one opaque element to
    /// a screen reader — the tap-to-inspect callout is unreachable by definition — so the shape a
    /// sighted reader gets at a glance (span, range, where it ended) is spelled out instead. Read
    /// aloud in place of the plot, not alongside it.
    /// </summary>
    public string AccessibleSummary
    {
        get
        {
            var reported = Points.Where(p => p.Value is not null).ToList();
            if (reported.Count == 0)
                return $"{Title}: no readings.";

            var low = reported.Min(p => p.Value!.Value);
            var high = reported.Max(p => p.Value!.Value);
            var latest = reported[^1];

            // The comparisons reach a screen reader through the summary, as on MetricTrendCard:
            // the canvas is one opaque element, so a rule and a band it draws are invisible to
            // anyone the plot itself is.
            var comparisons = string.Join(". ", new[] { BaselineText, ReferenceText }.Where(t => t is not null));

            return $"{Title}, {reported.Count} readings from {Points[0].Date:MMM d} to {Points[^1].Date:MMM d}. "
                + $"Ranging {FormatValue((double)low)} to {FormatValue((double)high)}. "
                + $"Latest {FormatValue((double)latest.Value!.Value)} on {latest.Date:MMM d}."
                + (comparisons.Length > 0 ? $" {comparisons}." : string.Empty);
        }
    }

    /// <summary>
    /// Null when the series can't be drawn as a line (fewer than two readings) — the caller keeps
    /// the old text summary for those instead of showing an empty plot.
    /// </summary>
    public static ChatChartItem? From(ChartSeries series)
    {
        if (series.Points.Count < 2)
            return null;

        // The server sends only days that have a reading; the chart wants one slot per calendar
        // day so gaps draw as its shaded no-data runs rather than silently compressing time.
        // Indexer assignment rather than ToDictionary, and min/max rather than first/last, so a
        // reply with duplicated or unordered dates degrades to a drawable chart, not a crash.
        var byDate = new Dictionary<DateOnly, double>();
        foreach (var point in series.Points)
            byDate[point.Date] = point.Value;

        var first = series.Points.Min(p => p.Date);
        var last = series.Points.Max(p => p.Date);

        // Chat activity windows are clamped to one week server-side (see DataQueryWhitelist); a
        // span wildly past that is malformed data, and the text summary reads better than a
        // chart that is mostly no-data shading.
        if (last.DayNumber - first.DayNumber > 62)
            return null;

        var points = new List<MetricPoint>();
        for (var date = first; date <= last; date = date.AddDays(1))
        {
            points.Add(new MetricPoint
            {
                Date = date,
                Value = byDate.TryGetValue(date, out var value) ? (decimal)value : null,
            });
        }

        if (points.Count < 2)
            return null;

        var values = series.Points.Select(p => p.Value).ToList();

        // The scale admits the comparisons the reply carried, the same way the Member Detail
        // trends' does — the axis labels then name the extent the marks are actually drawn
        // against, not just the readings'.
        var scale = TrendScale.For(
            values.Min(),
            values.Max(),
            series.Baseline,
            series.Reference is { } reference ? (double)reference.Low : null,
            series.Reference is { } high ? (double)high.High : null);

        return new ChatChartItem(
            series.Metric, points, scale, InkFor(series.Metric), series.Metric,
            series.Baseline is { } baseline ? (decimal)baseline : null, series.Reference);
    }

    /// <summary>
    /// The same identity inks the Member Detail trends wear, keyed on the series names
    /// <c>MemberChatService.BuildCharts</c> sends — so "Resting heart rate" is the same red line
    /// in chat that it is everywhere else. An unrecognised future series falls back to Primary
    /// rather than failing to draw.
    /// </summary>
    private static Color InkFor(string metric)
    {
        var key = metric switch
        {
            "Steps" => "MetricStepsInk",
            "Resting heart rate" => "MetricHeartInk",
            "Sleep" or "Sleep (minutes)" => "MetricSleepInk",
            _ => "Primary",
        };
        return MetricStatus.Resource(key, Colors.Blue);
    }
}

/// <summary>
/// How a chat reply spells a reading, in the one place both the drawn charts and the text
/// stand-in for an undrawable series can reach. Split out because the two had drifted: the charts
/// read sleep as "6h 12m" while the summary line beneath them read the same night as "372", which
/// is the stored unit and not a number any caregiver thinks in.
/// </summary>
internal static class ChatMetricFormat
{
    /// <summary>With the unit named — for prose and for the tap callout, which stand alone.</summary>
    internal static string WithUnit(string metric, double value) => metric switch
    {
        "Steps" => $"{value:#,##0} steps",
        "Resting heart rate" => $"{value:0} bpm",
        "Sleep" or "Sleep (minutes)" => Duration(value),
        _ => value.ToString("0.#"),
    };

    /// <summary>
    /// Without it — for the axis, where the series title a line above already says which reading
    /// this is and repeating the unit on both ends costs width it cannot spare. Sleep keeps its
    /// h/m shaping regardless: that is the number's readable form, not its unit.
    /// </summary>
    internal static string Bare(string metric, double value) => metric switch
    {
        "Steps" => $"{value:#,##0}",
        "Resting heart rate" => $"{value:0}",
        "Sleep" or "Sleep (minutes)" => Duration(value),
        _ => value.ToString("0.#"),
    };

    /// <summary>
    /// Mirrors <c>ReadingFigures.SleepFigure</c>, which the API uses for the same figure in
    /// prose. Duplicated rather than shared because this assembly cannot reference Infrastructure,
    /// and the two must not drift: a callout reading "8h 0m" beside a reply reading "8h" is the
    /// same night described two ways in one bubble.
    /// </summary>
    private static string Duration(double minutes)
    {
        var whole = (int)Math.Round(minutes);
        return whole switch
        {
            < 60 => $"{whole}m",
            _ when whole % 60 == 0 => $"{whole / 60}h",
            _ => $"{whole / 60}h {whole % 60}m",
        };
    }
}
