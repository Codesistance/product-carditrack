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

    public TrendLegendSwatch(TrendLegendMark mark)
    {
        WidthRequest = SwatchWidth;
        HeightRequest = SwatchHeight;
        VerticalOptions = LayoutOptions.Center;
        Drawable = new TrendLegendSwatchDrawable(mark);
    }
}

internal sealed class TrendLegendSwatchDrawable(TrendLegendMark mark) : IDrawable
{
    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        if (dirtyRect.Width <= 0 || dirtyRect.Height <= 0)
            return;

        if (mark == TrendLegendMark.Baseline)
        {
            var middle = dirtyRect.Center.Y;
            canvas.StrokeColor = TrendChartInk.Baseline;
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
