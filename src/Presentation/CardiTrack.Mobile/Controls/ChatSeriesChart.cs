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

    private readonly Label _title;
    private readonly TrendChart _chart;

    public ChatSeriesChart()
    {
        _title = new Label
        {
            FontFamily = "QuicksandSemiBold",
            FontSize = 11,
            TextColor = Microsoft.Maui.Controls.Application.Current?.Resources["BodyText"] as Color ?? Colors.Gray,
        };
        _chart = new TrendChart { HeightRequest = 72 };

        Content = new VerticalStackLayout
        {
            Spacing = 2,
            Children = { _title, _chart },
        };
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
        view._chart.Render(item.Points, item.Scale, item.Ink, showMarkers: true);
    }
}

/// <summary>
/// One reply series pre-shaped for drawing, following the same convention as <c>ChatTurnItem</c>
/// itself: every display decision is made at construction so the template stays converter-free
/// and <see cref="ChatSeriesChart"/> stays dumb.
/// </summary>
public sealed class ChatChartItem
{
    private ChatChartItem(string title, IReadOnlyList<MetricPoint> points, TrendScale scale, Color ink)
    {
        Title = title;
        Points = points;
        Scale = scale;
        Ink = ink;
    }

    public string Title { get; }
    public IReadOnlyList<MetricPoint> Points { get; }
    public TrendScale Scale { get; }
    public Color Ink { get; }

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

        // Chat activity windows are clamped to 14 days server-side (see DataQueryWhitelist); a
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
        var scale = TrendScale.For(values.Min(), values.Max(), baseline: null, referenceLow: null, referenceHigh: null);

        return new ChatChartItem(series.Metric, points, scale, InkFor(series.Metric));
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
            "Sleep (minutes)" => "MetricSleepInk",
            _ => "Primary",
        };
        return MetricStatus.Resource(key, Colors.Blue);
    }
}
