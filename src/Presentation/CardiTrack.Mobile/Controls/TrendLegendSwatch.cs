namespace CardiTrack.Mobile.Controls;

/// <summary>Which of a <see cref="TrendChart"/>'s two comparison marks a swatch stands for.</summary>
internal enum TrendLegendMark
{
    /// <summary>The dashed rule at this member's own learned normal.</summary>
    Baseline,

    /// <summary>The shaded band of the published typical-adult range.</summary>
    Reference,
}

/// <summary>
/// The 16×10 key beside a legend label on a <see cref="MetricTrendCard"/>, drawn with the same ink
/// and dash pattern as the mark it names (see <see cref="TrendChartInk"/>) — a legend that merely
/// approximated its chart would be a second thing to interpret rather than the answer to the first.
/// </summary>
internal sealed class TrendLegendSwatch : GraphicsView
{
    public const double SwatchWidth = 16;
    public const double SwatchHeight = 10;

    private readonly TrendLegendSwatchDrawable _drawable;

    public TrendLegendSwatch(TrendLegendMark mark)
    {
        WidthRequest = SwatchWidth;
        HeightRequest = SwatchHeight;
        VerticalOptions = LayoutOptions.Center;
        _drawable = new TrendLegendSwatchDrawable(mark);
        Drawable = _drawable;
    }

    /// <summary>
    /// The metric's own ink, for the baseline mark — set from the same value the card hands the
    /// chart, so the key is drawn in the colour of the rule it names. Ignored by the reference
    /// mark, which is neutral by design.
    /// </summary>
    public Color Ink
    {
        set
        {
            _drawable.Ink = value;
            Invalidate();
        }
    }
}

internal sealed class TrendLegendSwatchDrawable(TrendLegendMark mark) : IDrawable
{
    /// <summary>Null until the card this swatch belongs to has been bound to a metric.</summary>
    public Color? Ink { get; set; }

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        if (dirtyRect.Width <= 0 || dirtyRect.Height <= 0)
            return;

        if (mark == TrendLegendMark.Baseline)
        {
            var middle = dirtyRect.Center.Y;
            // Named for what it is rather than the shorter "ink": the reference branch below has
            // its own, and a pattern variable stays in scope past the branch that declared it.
            canvas.StrokeColor = Ink is { } metricInk
                ? TrendChartInk.BaselineIn(metricInk)
                : TrendChartInk.BaselineFallback;
            canvas.StrokeSize = TrendChartInk.BaselineThickness;
            canvas.StrokeDashPattern = TrendChartInk.BaselineDashes;
            canvas.DrawLine(dirtyRect.Left, middle, dirtyRect.Right, middle);
            canvas.StrokeDashPattern = null;
            return;
        }

        // The band at swatch scale is its fill between its two dashed edges — the same three
        // strokes the chart draws, just with the whole 10dp standing in for the range.
        var ink = TrendChartInk.Reference;
        canvas.FillColor = ink.WithAlpha(TrendChartInk.ReferenceFillAlpha);
        canvas.FillRectangle(dirtyRect);

        canvas.StrokeColor = ink.WithAlpha(TrendChartInk.ReferenceEdgeAlpha);
        canvas.StrokeSize = 1f;
        canvas.StrokeDashPattern = TrendChartInk.ReferenceEdgeDashes;
        canvas.DrawLine(dirtyRect.Left, dirtyRect.Top + 0.5f, dirtyRect.Right, dirtyRect.Top + 0.5f);
        canvas.DrawLine(dirtyRect.Left, dirtyRect.Bottom - 0.5f, dirtyRect.Right, dirtyRect.Bottom - 0.5f);
        canvas.StrokeDashPattern = null;
    }
}
