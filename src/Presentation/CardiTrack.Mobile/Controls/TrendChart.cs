using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Core.Charts;

namespace CardiTrack.Mobile.Controls;

/// <summary>
/// The line chart inside a <see cref="MetricTrendCard"/> — the Member Detail screen's daily series
/// at a size worth reading, where the row it replaced only had room for a 64×24 sparkline.
/// </summary>
/// <remarks>
/// A <see cref="GraphicsView"/> rather than a Polyline: the card is full width, so the geometry has
/// to be recomputed whenever the card is measured, and one draw call handles that natively instead
/// of a shape tree rebuilt from a SizeChanged handler.
/// </remarks>
public sealed class TrendChart : GraphicsView
{
    private readonly TrendChartDrawable _drawable = new();

    public TrendChart()
    {
        Drawable = _drawable;
    }

    /// <param name="points">The window to draw, oldest first; days with no reading carry a null value.</param>
    /// <param name="scale">
    /// The vertical extent to plot over. Passed in rather than derived here because the card
    /// labels its axis with the same two numbers, and a chart drawn to one extent beside labels
    /// naming another would be worse than either.
    /// </param>
    /// <param name="lineColor">The metric's status accent — see <see cref="MetricStatus.Accent"/>.</param>
    /// <param name="showMarkers">
    /// Whether to mark each reported day. A week's worth reads as data points; a month's worth
    /// reads as clutter, so the longer windows draw the line alone.
    /// </param>
    /// <param name="baseline">
    /// This member's own learned normal, drawn as a dashed rule across the window, or null for a
    /// metric that has none yet.
    /// </param>
    /// <param name="reference">
    /// The published typical-adult range, shaded behind the line, or null for a metric no
    /// standards body publishes one for.
    /// </param>
    public void Render(
        IReadOnlyList<MetricPoint> points,
        TrendScale scale,
        Color lineColor,
        bool showMarkers,
        decimal? baseline = null,
        MetricReference? reference = null)
    {
        _drawable.Points = points;
        _drawable.Scale = scale;
        _drawable.LineColor = lineColor;
        _drawable.ShowMarkers = showMarkers;
        _drawable.Baseline = baseline is { } value ? (double)value : null;
        _drawable.ReferenceLow = reference is not null ? (double)reference.Low : null;
        _drawable.ReferenceHigh = reference is not null ? (double)reference.High : null;
        Invalidate();
    }
}

/// <summary>
/// How the two things a reading is compared against are drawn — this member's own baseline, and
/// the published range behind it. Shared with <see cref="TrendLegendSwatch"/>, which has to draw
/// the same two marks at 16×10 for the key that names them.
/// </summary>
/// <remarks>
/// <para>
/// Neither is ever drawn in the metric's <em>status</em> accent: that accent means "how this
/// member is doing", and spending it on background would leave the eye picking the line out of
/// three things wearing the same colour as the reading moved between bands.
/// </para>
/// <para>
/// The baseline rule is drawn in the metric's own ink instead — the same fixed identity colour as
/// the line, which never changes with severity. The rule and the readings it judges are one
/// metric's story, and a neutral grey rule read as a third element the caregiver had to place
/// before they could use it. Dash pattern, half the stroke weight and
/// <see cref="BaselineAlpha"/> are what keep the two apart. The reference band stays neutral: it
/// is published by somebody else and belongs to no metric in particular.
/// </para>
/// </remarks>
internal static class TrendChartInk
{
    public const float ReferenceFillAlpha = 0.12f;
    public const float ReferenceEdgeAlpha = 0.5f;
    public const float BaselineAlpha = 0.7f;
    public const float BaselineThickness = 1.5f;

    public static readonly float[] ReferenceEdgeDashes = [3f, 3f];

    /// <summary>Longer than the band's edges: at a glance the dash tells the two marks apart.</summary>
    public static readonly float[] BaselineDashes = [6f, 4f];

    public static Color Reference => MetricStatus.Resource("ChartReferenceBand", Colors.Gray);

    /// <summary>
    /// The baseline rule in a metric's own ink. Taken from the caller rather than resolved here so
    /// the chart and the <see cref="TrendLegendSwatch"/> that names it can never be handed
    /// different colours.
    /// </summary>
    public static Color BaselineIn(Color metricInk) => metricInk.WithAlpha(BaselineAlpha);

    /// <summary>For a swatch drawn before its card has been bound to a metric.</summary>
    public static Color BaselineFallback =>
        MetricStatus.Resource("ChartBaselineRule", Colors.DarkGray).WithAlpha(BaselineAlpha);
}

/// <summary>
/// Draws one metric's window: gridlines, the reference band and baseline it is read against, the
/// line, its shaded area, and its markers.
/// </summary>
internal sealed class TrendChartDrawable : IDrawable
{
    /// <summary>Keeps the 3dp stroke and the markers clear of the top and bottom edges.</summary>
    private const float VerticalInset = 6f;

    /// <summary>
    /// Same, for the first and last day: they sit at the very ends of the axis, so without this the
    /// two markers a caregiver most wants — where the window starts, and today — draw half outside
    /// the canvas and come back clipped down the middle.
    /// </summary>
    private const float HorizontalInset = 6f;

    private const int GridRows = 4;
    private const float LineThickness = 3f;
    private const float MarkerRadius = 3.5f;
    private const float LatestMarkerRadius = 5f;

    public IReadOnlyList<MetricPoint> Points { get; set; } = [];

    /// <summary>The vertical extent to plot over, already fitted around the two rules below.</summary>
    public TrendScale Scale { get; set; }

    public Color LineColor { get; set; } = Colors.Gray;
    public bool ShowMarkers { get; set; }

    /// <summary>This member's own learned normal, or null when they have none for this metric.</summary>
    public double? Baseline { get; set; }

    public double? ReferenceLow { get; set; }
    public double? ReferenceHigh { get; set; }

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        if (dirtyRect.Width <= 0 || dirtyRect.Height <= 0 || Points.Count < 2)
            return;

        DrawGrid(canvas, dirtyRect);

        // The extent already accounts for the baseline and the reference band, which are plotted on
        // this same axis — and refuses them the room where making it would flatten the readings
        // into a straight line. See TrendScale.
        var scale = Scale;
        var range = (float)(scale.Max - scale.Min);
        var top = dirtyRect.Top + VerticalInset;
        var plotHeight = dirtyRect.Height - VerticalInset * 2;
        var bottom = top + plotHeight;
        var left = dirtyRect.Left + HorizontalInset;
        var plotWidth = dirtyRect.Width - HorizontalInset * 2;

        // A flat window has no range to normalise against, so it is drawn down the middle rather
        // than pinned to an edge, where it would read as a floor or a ceiling it never hit.
        float Y(double sample) => scale.HasExtent
            ? bottom - (float)(sample - scale.Min) / range * plotHeight
            : top + plotHeight / 2f;

        DrawReferenceBand(canvas, dirtyRect, scale, Y);
        DrawBaseline(canvas, dirtyRect, scale, Y);

        var line = new PathF();
        var area = new PathF();
        // Only the longer windows skip the per-day markers, and those are exactly the windows with
        // the most points to collect — so the list is not built at all unless it will be drawn.
        var markers = ShowMarkers ? new List<PointF>(Points.Count) : null;

        var latestMarker = default(PointF);
        var hasMarker = false;
        var lastKnown = 0d;
        var started = false;
        var lastX = 0f;

        for (var i = 0; i < Points.Count; i++)
        {
            var value = Points[i].Value;

            // A window that opens before this member's first reading starts the line where the
            // data does, rather than drawing a flat run at a value nobody recorded. Gaps *inside*
            // the line still bridge flat through their neighbours: a Polyline can't represent a
            // hole, and a broken line reads as a rendering fault, not a meaningful absence.
            if (value is null && !started)
                continue;

            if (value is { } v)
                lastKnown = (double)v;

            var x = left + plotWidth * i / (Points.Count - 1);
            var y = Y(lastKnown);

            if (!started)
            {
                line.MoveTo(x, y);
                area.MoveTo(x, bottom);
                area.LineTo(x, y);
                started = true;
            }
            else
            {
                line.LineTo(x, y);
                area.LineTo(x, y);
            }

            lastX = x;
            if (value is not null)
            {
                latestMarker = new PointF(x, y);
                hasMarker = true;
                markers?.Add(latestMarker);
            }
        }

        // A window whose every day is null has nothing to close the area against; the card shows
        // its "not enough readings" copy instead, but a resize can still land here first.
        if (!started)
            return;

        area.LineTo(lastX, bottom);
        area.Close();

        canvas.FillColor = LineColor.WithAlpha(0.14f);
        canvas.FillPath(area);

        canvas.StrokeColor = LineColor;
        canvas.StrokeSize = LineThickness;
        canvas.StrokeLineCap = LineCap.Round;
        canvas.StrokeLineJoin = LineJoin.Round;
        canvas.DrawPath(line);

        if (markers is not null)
        {
            foreach (var marker in markers)
                DrawMarker(canvas, marker, MarkerRadius);
        }

        // The most recent reading is always marked, whatever the window: it is the number the
        // card's headline value quotes, and the caregiver needs to see where it sits.
        if (hasMarker)
            DrawMarker(canvas, latestMarker, LatestMarkerRadius);
    }

    /// <summary>
    /// Shades the published typical-adult range across the full width, edge to edge — it is the
    /// backdrop the line is read against, so it runs behind the insets the line stops short of.
    /// </summary>
    /// <remarks>
    /// An end the plot could not make room for is drawn flush to the canvas edge rather than at
    /// the value: a band running off the chart reads as continuing past it, which is exactly what
    /// it does.
    /// </remarks>
    private void DrawReferenceBand(ICanvas canvas, RectF dirtyRect, TrendScale scale, Func<double, float> y)
    {
        if (ReferenceLow is not { } low || ReferenceHigh is not { } high || !scale.HasExtent)
            return;

        // What the plot shows of the range is its overlap with the plotted extent. A window that
        // sits entirely outside it shades nothing: filling to both edges instead would say every
        // reading on the chart was inside a range none of them reached.
        if (Math.Min(high, scale.Max) <= Math.Max(low, scale.Min))
            return;

        var bandTop = scale.Contains(high) ? y(high) : dirtyRect.Top;
        var bandBottom = scale.Contains(low) ? y(low) : dirtyRect.Bottom;
        if (bandBottom <= bandTop)
            return;

        var ink = TrendChartInk.Reference;
        canvas.FillColor = ink.WithAlpha(TrendChartInk.ReferenceFillAlpha);
        canvas.FillRectangle(dirtyRect.Left, bandTop, dirtyRect.Width, bandBottom - bandTop);

        canvas.StrokeColor = ink.WithAlpha(TrendChartInk.ReferenceEdgeAlpha);
        canvas.StrokeSize = 1f;
        canvas.StrokeDashPattern = TrendChartInk.ReferenceEdgeDashes;
        if (scale.Contains(high))
            canvas.DrawLine(dirtyRect.Left, bandTop, dirtyRect.Right, bandTop);
        if (scale.Contains(low))
            canvas.DrawLine(dirtyRect.Left, bandBottom, dirtyRect.Right, bandBottom);
        canvas.StrokeDashPattern = null;
    }

    /// <summary>
    /// Rules this member's own normal across the window, dashed and in the line's own ink so the
    /// rule and the readings it judges read as one metric — see <see cref="TrendChartInk"/>.
    /// Skipped when the plot could not make room for it — see <see cref="TrendScale.For"/> —
    /// where the legend carries the number instead.
    /// </summary>
    private void DrawBaseline(ICanvas canvas, RectF dirtyRect, TrendScale scale, Func<double, float> y)
    {
        if (Baseline is not { } baseline || !scale.HasExtent || !scale.Contains(baseline))
            return;

        var at = y(baseline);
        canvas.StrokeColor = TrendChartInk.BaselineIn(LineColor);
        canvas.StrokeSize = TrendChartInk.BaselineThickness;
        canvas.StrokeDashPattern = TrendChartInk.BaselineDashes;
        canvas.DrawLine(dirtyRect.Left, at, dirtyRect.Right, at);
        canvas.StrokeDashPattern = null;
    }

    private void DrawMarker(ICanvas canvas, PointF at, float radius)
    {
        canvas.FillColor = Colors.White;
        canvas.FillCircle(at.X, at.Y, radius);
        canvas.StrokeColor = LineColor;
        canvas.StrokeSize = 2f;
        canvas.DrawCircle(at.X, at.Y, radius);
    }

    private static void DrawGrid(ICanvas canvas, RectF dirtyRect)
    {
        canvas.StrokeColor = MetricStatus.Resource("Divider", Colors.LightGray);
        canvas.StrokeSize = 1f;

        for (var i = 0; i <= GridRows; i++)
        {
            var y = dirtyRect.Top + dirtyRect.Height * i / GridRows;
            canvas.DrawLine(dirtyRect.Left, y, dirtyRect.Right, y);
        }
    }
}
