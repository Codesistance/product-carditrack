using CardiTrack.Application.DTOs.Responses;

namespace CardiTrack.Mobile.Controls;

/// <summary>
/// The gauge across a <see cref="MetricCard"/> that shows where today's reading sits against the
/// two things it is read against: this member's own learned baseline, and the published
/// typical-adult range.
/// </summary>
/// <remarks>
/// <para>
/// Horizontal, and not a sparkline. A tile is half the screen wide, which is not enough room for a
/// month of daily points to say anything — and the comparison, not the history, is what the tile is
/// for. Member Detail's <see cref="TrendChart"/> is where the series belongs.
/// </para>
/// <para>
/// The marks are drawn in <see cref="TrendChartInk"/>, the same ink that chart uses, so a dashed
/// rule means "their own normal" on both screens rather than one thing on the dashboard and
/// another a tap away.
/// </para>
/// </remarks>
public sealed class BaselineStrip : GraphicsView
{
    /// <summary>Tall enough for the band to read as a band rather than a line.</summary>
    public const double StripHeight = 26;

    private readonly BaselineStripDrawable _drawable = new();

    public BaselineStrip()
    {
        HeightRequest = StripHeight;
        Drawable = _drawable;
    }

    /// <summary>
    /// Renders one metric, and reports whether there was anything to render — false when the metric
    /// has no reading, or nothing to read it against, in which case the caller hides the strip
    /// rather than showing an empty rail.
    /// </summary>
    /// <param name="accent">
    /// The metric's status colour, spent on today's marker alone. The baseline and the band stay
    /// neutral for the reason <see cref="TrendChartInk"/> gives: an accent on the background would
    /// leave the eye picking the reading out of three things wearing the same colour.
    /// </param>
    public bool Apply(DashboardMetric metric, Color accent)
    {
        var reference = metric.Reference;
        var value = metric.Value is { } v ? (double)v : (double?)null;
        var baseline = metric.Baseline is { } b ? (double)b : (double?)null;
        var referenceLow = reference is not null ? (double)reference.Low : (double?)null;
        var referenceHigh = reference is not null ? (double)reference.High : (double?)null;

        // Nothing to compare against is not the same as a quiet day: a rail with a single dot on it
        // would imply a comparison the metric cannot make. SpO2 and breathing rate have no baseline,
        // activity and skin temperature have no published range — but every metric has at least one
        // of the two, so this only bites before the first reading lands.
        if (value is null || (baseline is null && referenceLow is null))
            return false;

        var window = Window(value, baseline, referenceLow, referenceHigh,
            metric.Goal is { } goal ? (double)goal : null);

        _drawable.Min = window.Min;
        _drawable.Max = window.Max;
        _drawable.Value = value;
        _drawable.Baseline = baseline;
        _drawable.ReferenceLow = referenceLow;
        _drawable.ReferenceHigh = referenceHigh;
        _drawable.Accent = accent;
        Invalidate();
        return true;
    }

    /// <summary>
    /// The span the strip plots over: everything it has to draw, plus a margin so no mark ends up
    /// welded to an edge. The goal joins the calculation without being drawn — an activity tile
    /// whose window stopped at today's steps would put a member at 40% of their goal hard against
    /// the right-hand end, reading as "at the top of the range" when it is the opposite.
    /// </summary>
    private static (double Min, double Max) Window(
        double? value, double? baseline, double? referenceLow, double? referenceHigh, double? goal)
    {
        var marks = new[] { value, baseline, referenceLow, referenceHigh, goal }
            .Where(m => m is not null)
            .Select(m => m!.Value)
            .ToList();

        var min = marks.Min();
        var max = marks.Max();

        // A window with no width cannot be normalised against. Widening it around the value keeps
        // the marks centred rather than pinning them to an end.
        if (max - min < double.Epsilon)
        {
            var nudge = Math.Abs(max) > double.Epsilon ? Math.Abs(max) * 0.1 : 1;
            return (min - nudge, max + nudge);
        }

        var padding = (max - min) * 0.15;
        return (min - padding, max + padding);
    }
}

/// <summary>
/// Draws the band, the baseline rule and today's marker along a horizontal axis. Deliberately
/// axis-free: the numbers are named in the legend and the value above it, and gridlines at this
/// size would be three more things to look at for no reading gained.
/// </summary>
internal sealed class BaselineStripDrawable : IDrawable
{
    /// <summary>Keeps the marker's full radius on the canvas at either end of the window.</summary>
    private const float HorizontalInset = 7f;

    private const float RailThickness = 3f;
    private const float MarkerRadius = 5f;
    private const float MarkerRingThickness = 2f;

    public double Min { get; set; }
    public double Max { get; set; }
    public double? Value { get; set; }
    public double? Baseline { get; set; }
    public double? ReferenceLow { get; set; }
    public double? ReferenceHigh { get; set; }
    public Color Accent { get; set; } = Colors.Gray;

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        var range = Max - Min;
        if (dirtyRect.Width <= 0 || dirtyRect.Height <= 0 || range <= 0)
            return;

        var left = dirtyRect.Left + HorizontalInset;
        var plotWidth = dirtyRect.Width - HorizontalInset * 2;
        var middle = dirtyRect.Center.Y;

        float X(double sample) => left + (float)((sample - Min) / range) * plotWidth;

        DrawRail(canvas, left, plotWidth, middle);
        DrawReferenceBand(canvas, dirtyRect, X);
        DrawBaseline(canvas, dirtyRect, X);
        DrawMarker(canvas, X, middle);
    }

    /// <summary>The axis itself, so a strip still reads as a scale where the band does not reach.</summary>
    private static void DrawRail(ICanvas canvas, float left, float plotWidth, float middle)
    {
        canvas.StrokeColor = MetricStatus.Resource("ProgressTrack", Colors.LightGray);
        canvas.StrokeSize = RailThickness;
        canvas.StrokeLineCap = LineCap.Round;
        canvas.DrawLine(left, middle, left + plotWidth, middle);
        canvas.StrokeLineCap = LineCap.Butt;
    }

    private void DrawReferenceBand(ICanvas canvas, RectF dirtyRect, Func<double, float> x)
    {
        if (ReferenceLow is not { } low || ReferenceHigh is not { } high)
            return;

        // Ordered rather than assumed: the published ranges are ours and are written low-then-high,
        // but a range that ever arrived inverted would make this a negative-width rectangle, which
        // renders as nothing or as glitch depending on the backend. Cheaper to order the two ends
        // than to rely on every future producer of a MetricReference getting them the right way round.
        var start = Math.Min(x(low), x(high));
        var end = Math.Max(x(low), x(high));
        var band = new RectF(start, dirtyRect.Top, end - start, dirtyRect.Height);
        var ink = TrendChartInk.Reference;

        canvas.FillColor = ink.WithAlpha(TrendChartInk.ReferenceFillAlpha);
        canvas.FillRectangle(band);

        // Vertical edges here where the chart's are horizontal: the axis is turned on its side, so
        // the range's two ends are its left and right rather than its top and bottom.
        canvas.StrokeColor = ink.WithAlpha(TrendChartInk.ReferenceEdgeAlpha);
        canvas.StrokeSize = 1f;
        canvas.StrokeDashPattern = TrendChartInk.ReferenceEdgeDashes;
        canvas.DrawLine(start, dirtyRect.Top, start, dirtyRect.Bottom);
        canvas.DrawLine(end, dirtyRect.Top, end, dirtyRect.Bottom);
        canvas.StrokeDashPattern = null;
    }

    private void DrawBaseline(ICanvas canvas, RectF dirtyRect, Func<double, float> x)
    {
        if (Baseline is not { } baseline)
            return;

        var at = x(baseline);
        canvas.StrokeColor = TrendChartInk.Baseline;
        canvas.StrokeSize = TrendChartInk.BaselineThickness;
        canvas.StrokeDashPattern = TrendChartInk.BaselineDashes;
        canvas.DrawLine(at, dirtyRect.Top, at, dirtyRect.Bottom);
        canvas.StrokeDashPattern = null;
    }

    /// <summary>
    /// Today's reading. Ringed in the card's own white so it stays legible where it lands on top of
    /// the baseline rule — which is exactly where a settled member's reading sits.
    /// </summary>
    private void DrawMarker(ICanvas canvas, Func<double, float> x, float middle)
    {
        if (Value is not { } value)
            return;

        var at = x(value);

        canvas.StrokeColor = Colors.White;
        canvas.StrokeSize = MarkerRingThickness;
        canvas.DrawCircle(at, middle, MarkerRadius);

        canvas.FillColor = Accent;
        canvas.FillCircle(at, middle, MarkerRadius);
    }
}
