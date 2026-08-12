using CardiTrack.Application.DTOs.Responses;

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
    /// <param name="lineColor">The metric's status accent — see <see cref="MetricStatus.Accent"/>.</param>
    /// <param name="showMarkers">
    /// Whether to mark each reported day. A week's worth reads as data points; a month's worth
    /// reads as clutter, so the longer windows draw the line alone.
    /// </param>
    public void Render(IReadOnlyList<MetricPoint> points, Color lineColor, bool showMarkers)
    {
        _drawable.Points = points;
        _drawable.LineColor = lineColor;
        _drawable.ShowMarkers = showMarkers;
        Invalidate();
    }
}

/// <summary>Draws one metric's window: gridlines, the line, its shaded area, and its markers.</summary>
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
    public Color LineColor { get; set; } = Colors.Gray;
    public bool ShowMarkers { get; set; }

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        if (dirtyRect.Width <= 0 || dirtyRect.Height <= 0 || Points.Count < 2)
            return;

        DrawGrid(canvas, dirtyRect);

        var known = Points.Where(p => p.Value is not null).Select(p => (float)p.Value!.Value).ToList();
        if (known.Count < 2)
            return;

        var min = known.Min();
        var range = known.Max() - min;
        var top = dirtyRect.Top + VerticalInset;
        var plotHeight = dirtyRect.Height - VerticalInset * 2;
        var bottom = top + plotHeight;
        var left = dirtyRect.Left + HorizontalInset;
        var plotWidth = dirtyRect.Width - HorizontalInset * 2;

        // A flat window has no range to normalise against, so it is drawn down the middle rather
        // than pinned to an edge, where it would read as a floor or a ceiling it never hit.
        float Y(float sample) => range > 0
            ? bottom - (sample - min) / range * plotHeight
            : top + plotHeight / 2f;

        var line = new PathF();
        var area = new PathF();
        var markers = new List<PointF>();

        var lastKnown = 0f;
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
                lastKnown = (float)v;

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
                markers.Add(new PointF(x, y));
        }

        area.LineTo(lastX, bottom);
        area.Close();

        canvas.FillColor = LineColor.WithAlpha(0.14f);
        canvas.FillPath(area);

        canvas.StrokeColor = LineColor;
        canvas.StrokeSize = LineThickness;
        canvas.StrokeLineCap = LineCap.Round;
        canvas.StrokeLineJoin = LineJoin.Round;
        canvas.DrawPath(line);

        if (ShowMarkers)
        {
            foreach (var marker in markers)
                DrawMarker(canvas, marker, MarkerRadius);
        }

        // The most recent reading is always marked, whatever the window: it is the number the
        // card's headline value quotes, and the caregiver needs to see where it sits.
        if (markers.Count > 0)
            DrawMarker(canvas, markers[^1], LatestMarkerRadius);
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
