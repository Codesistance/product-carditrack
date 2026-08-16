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

    /// <summary>
    /// Whether this window has any day with no reading, given at least one day that has — the runs
    /// <see cref="TrendChartDrawable"/> shades.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Asked by <see cref="MetricTrendCard"/> so the key names the shade only on a window that
    /// carries one. It reads the same points the chart is handed, on the same rule, so the two
    /// cannot disagree about whether the chart has a gap on it — and if they did, the key would be
    /// the one that was wrong, since the chart draws from the geometry.
    /// </para>
    /// <para>
    /// Days before the member's first reading count. They did not when the marks were break marks
    /// bounding a bridge — the line starts where the data does, so there was no bridge there to
    /// explain — but the shade means "nothing was recorded here", which is exactly what those days
    /// are, and it is what explains a line that starts partway across the window.
    /// </para>
    /// </remarks>
    public static bool HasMissingDays(IReadOnlyList<MetricPoint> points)
    {
        var reported = false;
        var missing = false;

        foreach (var point in points)
        {
            if (point.Value is not null)
                reported = true;
            else
                missing = true;
        }

        // No reading at all is not a window with gaps in it; the card shows its own "not enough
        // readings" copy and never draws a chart to key.
        return reported && missing;
    }

    /// <summary>
    /// The shaded run of missing days under <paramref name="x"/>, or null if the tap landed on the
    /// data. In the chart's own coordinates — the caller converts from the gesture.
    /// </summary>
    /// <remarks>
    /// Answered from the geometry the last draw actually produced rather than recomputed from the
    /// points, so what a tap hits is by construction what the caregiver sees. A chart that has not
    /// been drawn yet has no spans and answers null.
    /// </remarks>
    internal NoDataSpan? NoDataSpanAt(double x)
    {
        foreach (var span in _drawable.NoDataSpans)
        {
            if (x >= span.Left && x <= span.Right)
                return span;
        }

        return null;
    }
}

/// <summary>
/// One shaded run of days with no reading: where it was drawn, and which days it covers.
/// </summary>
internal readonly record struct NoDataSpan(
    float Left, float Right, DateOnly From, DateOnly To, int Days, bool Bridged)
{
    /// <summary>
    /// What a tap on the run says. Names the days rather than counting them alone: "no readings
    /// for 3 days" leaves the caregiver working out which three, and the answer is usually the
    /// thing they want — a weekend the watch was off the wrist reads differently from three days
    /// mid-week.
    /// </summary>
    /// <remarks>
    /// The second sentence differs because the drawing does. A run inside the window is bridged:
    /// the line is carried flat across it, and saying so is what stops that flat stretch being
    /// read as a genuinely steady week. A run before the first reading is not bridged — the line
    /// starts where the data does — so telling the caregiver it was drawn across would describe a
    /// stroke that is not on their screen.
    /// </remarks>
    public string Description
    {
        get
        {
            var days = Days == 1
                ? $"No readings on {From:MMM d}."
                : $"No readings from {From:MMM d} to {To:MMM d} — {Days} days.";

            var line = Bridged
                ? Days == 1 ? " The line is drawn straight across it." : " The line is drawn straight across them."
                : " The line starts where the readings do.";

            return days + line;
        }
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

    /// <summary>
    /// The run into a day still in progress. Shorter and tighter than the baseline's dashes, and
    /// drawn at the line's own weight rather than the rule's, so it reads as the same line
    /// continuing provisionally — not as a third kind of mark on the chart.
    /// </summary>
    public static readonly float[] PartialDayDashes = [2f, 2f];

    /// <summary>
    /// The bounds of a run of days with no reading: a light dashed rule at each end of the run,
    /// with the days between them shaded.
    /// </summary>
    /// <remarks>
    /// This was a zigzag — the broken-axis symbol from print — drawn at each end. It said the right
    /// thing and said it too loudly: two full-height marks cutting across the plot competed with
    /// the line for the eye on a chart whose whole subject is that line, and on a 30-day window
    /// with two or three gaps the chart read as mostly marks. Shading the run instead puts the
    /// absence behind the data where it belongs — the region is the statement, and the rules only
    /// bound it.
    /// </remarks>
    public const float NoDataThickness = 1f;

    /// <summary>Light and open, so the bound reads as an edge rather than as another reading.</summary>
    public static readonly float[] NoDataBoundDashes = [3f, 3f];

    /// <summary>
    /// How strongly the missing run is shaded, and how strongly its edges are ruled. Low enough
    /// that the gridlines still show through it: the shade marks a region, it does not black it out.
    /// </summary>
    public const float NoDataFillAlpha = 0.13f;

    public const float NoDataBoundAlpha = 0.5f;

    public static Color Reference => MetricStatus.Resource("ChartReferenceBand", Colors.Gray);

    public static Color NoData => MetricStatus.Resource("ChartNoDataMark", Colors.Gray);

    /// <summary>
    /// The shade for a missing run, opposite the metric's own ink on the colour wheel.
    /// </summary>
    /// <remarks>
    /// Derived from the ink rather than fixed, because the six metrics run blue, red, purple,
    /// green and two more, and any one fixed shade would sit near one of them: a wash that reads
    /// as "no data" on the steps chart would read as a second heart-rate series on the heart one.
    /// Rotating the hue half a turn guarantees the largest separation from whatever ink it is
    /// drawn under, for every metric, including the ones not added yet. Saturation and lightness
    /// are pinned rather than carried over so a pale ink cannot produce a shade too faint to see.
    /// </remarks>
    public static Color NoDataShadeFor(Color metricInk) =>
        Color.FromHsla((metricInk.GetHue() + 0.5f) % 1f, 0.5f, 0.5f);

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

    private readonly List<NoDataSpan> _noDataSpans = [];

    /// <summary>
    /// Where each shaded run of missing days landed on the canvas, in the order they were drawn.
    /// Recorded during <see cref="Draw"/> because that is where the geometry exists — the caller
    /// needs it to answer a tap on one. See <see cref="TrendChart.NoDataSpanAt"/>.
    /// </summary>
    public IReadOnlyList<NoDataSpan> NoDataSpans => _noDataSpans;

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
        // Rebuilt every pass: the spans are canvas geometry, so a resize or a new window makes the
        // ones from last time wrong rather than stale.
        _noDataSpans.Clear();

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
        DrawNoDataBounds(canvas, dirtyRect, left, plotWidth);

        var line = new PathF();
        // The run into a day that has not finished yet, drawn apart from the rest — see
        // MetricPoint.IsPartial. Empty for every window that ends on a completed day.
        var partial = new PathF();
        var area = new PathF();
        // Only the longer windows skip the per-day markers, and those are exactly the windows with
        // the most points to collect — so the list is not built at all unless it will be drawn.
        var markers = ShowMarkers ? new List<PointF>(Points.Count) : null;

        var latestMarker = default(PointF);
        var hasMarker = false;
        var lastKnown = 0d;
        var started = false;
        var lastX = 0f;
        var lastSettled = default(PointF);
        var hasSettled = false;
        var partialStarted = false;
        // Where the previous point was plotted, so the dashed run can start on the solid line
        // rather than in mid-air.
        var previous = default(PointF);

        for (var i = 0; i < Points.Count; i++)
        {
            var value = Points[i].Value;
            var isPartial = Points[i].IsPartial;

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
            var point = new PointF(x, y);

            if (!started)
            {
                line.MoveTo(x, y);
                area.MoveTo(x, bottom);
                area.LineTo(x, y);
                started = true;
            }
            else if (isPartial)
            {
                // The dashed run starts where the solid line stopped, so the two paths meet rather
                // than leaving a gap.
                if (!partialStarted)
                {
                    partial.MoveTo(previous.X, previous.Y);
                    partialStarted = true;
                }
                partial.LineTo(x, y);
            }
            else
            {
                line.LineTo(x, y);
                area.LineTo(x, y);
            }

            previous = point;

            if (!isPartial)
            {
                lastX = x;
                if (value is not null)
                {
                    lastSettled = point;
                    hasSettled = true;
                }
            }

            if (value is not null)
            {
                latestMarker = point;
                hasMarker = true;
                // A running total is not one of the readings the window is made of, so it does not
                // get a day marker — the dashed run is what says it is there.
                if (!isPartial)
                    markers?.Add(point);
            }
        }

        // A window whose every day is null has nothing to close the area against; the card shows
        // its "not enough readings" copy instead, but a resize can still land here first.
        if (!started)
            return;

        canvas.StrokeColor = LineColor;
        canvas.StrokeSize = LineThickness;
        canvas.StrokeLineCap = LineCap.Round;
        canvas.StrokeLineJoin = LineJoin.Round;

        // The shaded area stops at the last finished day. Filling under a half-collected total
        // would give it the same visual weight as the days either side of it — and a window whose
        // only reading *is* that partial day has no finished run to shade at all.
        if (hasSettled)
        {
            area.LineTo(lastX, bottom);
            area.Close();

            canvas.FillColor = LineColor.WithAlpha(0.14f);
            canvas.FillPath(area);
            canvas.DrawPath(line);
        }

        if (partialStarted)
        {
            canvas.StrokeDashPattern = TrendChartInk.PartialDayDashes;
            canvas.DrawPath(partial);
            canvas.StrokeDashPattern = null;
        }

        if (markers is not null)
        {
            foreach (var marker in markers)
                DrawMarker(canvas, marker, MarkerRadius);
        }

        // The most recent reading is always marked, whatever the window: it is the number the
        // card's headline value quotes, and the caregiver needs to see where it sits. Where the
        // window ends on a day in progress the headline quotes the last finished day instead, so
        // that is the point the emphasis belongs to.
        if (hasSettled)
            DrawMarker(canvas, lastSettled, LatestMarkerRadius);
        else if (hasMarker)
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
    /// Shades each run of days with no reading and rules its ends, so the flat run the line draws
    /// through them is legible as a bridge rather than as a week the member's heart rate genuinely
    /// held still.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The line itself is left alone. Breaking it was the other option and is worse: a gap in a
    /// stroke reads as a rendering fault, and the caregiver would be left deciding whether the app
    /// was broken before they could read the chart. Marking the boundary says the same thing
    /// without asking that question.
    /// </para>
    /// <para>
    /// The marks bound the missing days themselves, halfway to the reported day either side, rather
    /// than standing on those readings: a one-day gap would otherwise put both marks on the same
    /// pixel, and a mark drawn on a reported day would sit on top of its own marker. A run still
    /// missing at the end of the window gets an opening mark only — it has no closing reading, and
    /// running off the edge is exactly what it does.
    /// </para>
    /// <para>
    /// Days before this member's first reading are not marked: the line starts where the data does
    /// (see <see cref="Draw"/>), so there is no bridged run there to explain.
    /// </para>
    /// </remarks>
    private void DrawNoDataBounds(ICanvas canvas, RectF dirtyRect, float left, float plotWidth)
    {
        var first = -1;
        for (var i = 0; i < Points.Count && first < 0; i++)
        {
            if (Points[i].Value is not null)
                first = i;
        }

        if (first < 0)
            return;

        var shade = TrendChartInk.NoDataShadeFor(LineColor);

        float X(int index) => left + plotWidth * index / (Points.Count - 1);

        // The days before the member's first reading. The old break marks skipped these on the
        // grounds that the line starts where the data does, so there was no bridged run to fence —
        // true of a mark that meant "the line here was drawn, not measured", and false of a shade
        // that means "nothing was recorded here". They are days with no readings like any other,
        // and shading them is what explains why the line starts partway across the window instead
        // of at the left edge. Open at the left for the same reason a run still missing at the end
        // is open at the right: it runs off the window, which is what it does. It closes on the
        // first reading rather than short of it, the same rule the runs below follow — the shade
        // ends where the measuring starts.
        if (first > 0)
        {
            var opensAt = X(first);
            canvas.FillColor = shade.WithAlpha(TrendChartInk.NoDataFillAlpha);
            canvas.FillRectangle(dirtyRect.Left, dirtyRect.Top, opensAt - dirtyRect.Left, dirtyRect.Height);

            canvas.StrokeColor = shade.WithAlpha(TrendChartInk.NoDataBoundAlpha);
            canvas.StrokeSize = TrendChartInk.NoDataThickness;
            canvas.StrokeDashPattern = TrendChartInk.NoDataBoundDashes;
            canvas.StrokeLineCap = LineCap.Butt;
            canvas.DrawLine(opensAt, dirtyRect.Top, opensAt, dirtyRect.Bottom);
            canvas.StrokeDashPattern = null;

            _noDataSpans.Add(new NoDataSpan(
                dirtyRect.Left, opensAt, Points[0].Date, Points[first - 1].Date, first, Bridged: false));
        }

        var at = first + 1;
        while (at < Points.Count)
        {
            if (Points[at].Value is not null)
            {
                at++;
                continue;
            }

            var runStart = at;
            while (at < Points.Count && Points[at].Value is null)
                at++;
            var runEnd = at - 1;

            // Reading to reading, not midpoint to midpoint. What is being marked is the stretch of
            // line that was interpolated, and that stretch runs from the last real reading to the
            // next one — the whole segment between them was drawn rather than measured, so the
            // whole segment is what the shade covers. The midpoints were inherited from the break
            // marks this replaced, which bounded the missing days themselves because a mark
            // standing on a reported day would have sat on top of its own marker; a shaded region
            // has no such problem, and stopping halfway left the two segments either side of a gap
            // looking measured when they were not.
            var from = X(runStart - 1);
            // A run still missing at the end of the window has no closing reading to meet, so it
            // runs to the edge — which is what it does.
            var openEnded = runEnd >= Points.Count - 1;
            var to = openEnded ? dirtyRect.Right : X(runEnd + 1);

            canvas.FillColor = shade.WithAlpha(TrendChartInk.NoDataFillAlpha);
            canvas.FillRectangle(from, dirtyRect.Top, to - from, dirtyRect.Height);

            canvas.StrokeColor = shade.WithAlpha(TrendChartInk.NoDataBoundAlpha);
            canvas.StrokeSize = TrendChartInk.NoDataThickness;
            canvas.StrokeDashPattern = TrendChartInk.NoDataBoundDashes;
            canvas.StrokeLineCap = LineCap.Butt;
            canvas.DrawLine(from, dirtyRect.Top, from, dirtyRect.Bottom);
            if (!openEnded)
                canvas.DrawLine(to, dirtyRect.Top, to, dirtyRect.Bottom);
            canvas.StrokeDashPattern = null;

            _noDataSpans.Add(new NoDataSpan(
                from, to, Points[runStart].Date, Points[runEnd].Date, runEnd - runStart + 1, Bridged: true));
        }
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
