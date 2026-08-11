using CardiTrack.Application.DTOs.Responses;
using Microsoft.Maui.Controls.Shapes;
// CardiTrack.Application shadows MAUI's Application in any file importing it — see NudgeMiniRow.
using MauiApplication = Microsoft.Maui.Controls.Application;

namespace CardiTrack.Mobile.Controls;

/// <summary>
/// One row of the Member Detail screen's "trend of all key metrics" section (issue #188): icon,
/// name, latest value, and a minimal 7-day sparkline. Built in code rather than XAML, like
/// <see cref="NudgeMiniRow"/> — a single row with no template, created once per metric the
/// member actually reports.
/// </summary>
public sealed class MetricTrendRow : Grid
{
    private const double SparklineWidth = 64;
    private const double SparklineHeight = 24;

    public MetricTrendRow(string iconSource, string name, string valueText, DashboardMetric metric)
    {
        ColumnDefinitions =
        [
            new ColumnDefinition(GridLength.Auto),
            new ColumnDefinition(GridLength.Star),
            new ColumnDefinition(new GridLength(SparklineWidth + 4)),
        ];
        ColumnSpacing = 10;

        var icon = new Image
        {
            Source = iconSource,
            WidthRequest = 20,
            HeightRequest = 20,
            VerticalOptions = LayoutOptions.Center,
        };

        var nameLabel = new Label { Text = name, VerticalOptions = LayoutOptions.End };
        ApplyStyle(nameLabel, "Body2Medium");
        var valueLabel = new Label { Text = valueText, VerticalOptions = LayoutOptions.Start };
        ApplyStyle(valueLabel, "Body2");
        valueLabel.TextColor = Resource("MutedText", Colors.Gray);

        var textStack = new VerticalStackLayout { Spacing = 1, VerticalOptions = LayoutOptions.Center };
        textStack.Add(nameLabel);
        textStack.Add(valueLabel);

        var sparkline = BuildSparkline(metric.Series, StatusColor(metric.Status));

        // Grid's own Add(IView) (from Layout.Children) shadows the GridExtensions.Add(view,
        // column, row) overload when called from inside a Grid subclass, so the column has to
        // be set as an attached property first.
        SetColumn((BindableObject)icon, 0);
        SetColumn((BindableObject)textStack, 1);
        SetColumn((BindableObject)sparkline, 2);
        Children.Add(icon);
        Children.Add(textStack);
        Children.Add(sparkline);
    }

    /// <summary>
    /// A missing day bridges flat through its neighbours rather than breaking the line — a
    /// Polyline can't represent a gap, and a broken line reads as a rendering bug, not a
    /// meaningful absence, at this size.
    /// </summary>
    private static View BuildSparkline(List<MetricPoint> series, Color color)
    {
        var known = series.Where(p => p.Value is not null).Select(p => (double)p.Value!.Value).ToList();
        if (known.Count < 2)
            return new BoxView { WidthRequest = SparklineWidth, HeightRequest = SparklineHeight, IsVisible = false };

        var min = known.Min();
        var range = known.Max() - min;
        var lastKnown = known[0];

        var points = new PointCollection();
        for (var i = 0; i < series.Count; i++)
        {
            var x = series.Count > 1 ? SparklineWidth * i / (series.Count - 1) : 0;
            if (series[i].Value is { } v)
                lastKnown = (double)v;
            var normalized = range > 0 ? (lastKnown - min) / range : 0.5;
            var y = SparklineHeight - normalized * SparklineHeight;
            points.Add(new Point(x, y));
        }

        return new Polyline
        {
            Points = points,
            Stroke = new SolidColorBrush(color),
            StrokeThickness = 2,
            StrokeLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            WidthRequest = SparklineWidth,
            HeightRequest = SparklineHeight,
            VerticalOptions = LayoutOptions.Center,
        };
    }

    private static Color StatusColor(string status) => status switch
    {
        "green" => Resource("StatusGreen", Colors.Green),
        "yellow" => Resource("StatusYellow", Colors.Orange),
        "orange" => Resource("StatusOrange", Colors.OrangeRed),
        "red" => Resource("StatusRed", Colors.Red),
        _ => Resource("StatusUnknown", Colors.Gray),
    };

    private static Color Resource(string key, Color fallback) =>
        MauiApplication.Current?.Resources.TryGetValue(key, out var value) == true && value is Color colour
            ? colour
            : fallback;

    private static void ApplyStyle(Label label, string key)
    {
        if (MauiApplication.Current?.Resources.TryGetValue(key, out var value) == true && value is Style style)
            label.Style = style;
    }
}
