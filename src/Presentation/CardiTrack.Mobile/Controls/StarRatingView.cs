namespace CardiTrack.Mobile.Controls;

/// <summary>
/// A metric's rating against the member's own normal, drawn as five stars: the earned ones filled
/// in that metric's status colour, the rest left as an empty outline.
/// </summary>
/// <remarks>
/// <para>
/// Drawn rather than assembled from <c>icon_star.svg</c> images, because the fill has to change
/// colour per reading and MAUI has no runtime tint for an <see cref="Image"/> — the row used to be
/// five copies of one grey glyph separated only by opacity, which at 15dp made a four-star reading
/// and a one-star reading look near enough identical.
/// </para>
/// <para>
/// One <see cref="GraphicsView"/> for the whole row rather than five, matching
/// <see cref="TrendLegendSwatch"/>: the row carries a single semantic description, so five hosted
/// views would be five accessibility stops for one value.
/// </para>
/// </remarks>
public sealed class StarRatingView : GraphicsView
{
    public const int StarCount = 5;

    private const double StarSize = 15;
    private const double StarGap = 3;

    private readonly StarRatingDrawable _drawable = new();

    public StarRatingView()
    {
        WidthRequest = (StarSize * StarCount) + (StarGap * (StarCount - 1));
        HeightRequest = StarSize;
        HorizontalOptions = LayoutOptions.Start;
        Drawable = _drawable;
    }

    /// <param name="filled">Stars earned, 0-<see cref="StarCount"/>.</param>
    /// <param name="ink">
    /// The fill for the earned stars — the metric's own status accent, so the stars and the status
    /// pill beside them read as one judgement rather than two.
    /// </param>
    public void Render(int filled, Color ink)
    {
        _drawable.Filled = Math.Clamp(filled, 0, StarCount);
        _drawable.Ink = ink;
        Invalidate();
    }

    private sealed class StarRatingDrawable : IDrawable
    {
        /// <summary>
        /// The unearned stars. Neutral and light: they are the ceiling the rating is read against,
        /// not a second reading, so they must not compete with the filled ones for attention.
        /// </summary>
        private static readonly Color EmptyInk = Color.FromArgb("#C3CAD3");

        public int Filled { get; set; }

        public Color Ink { get; set; } = Colors.Gray;

        public void Draw(ICanvas canvas, RectF dirtyRect)
        {
            if (dirtyRect.Width <= 0 || dirtyRect.Height <= 0)
                return;

            var size = (float)Math.Min(StarSize, dirtyRect.Height);
            var radius = size / 2f;
            var centerY = dirtyRect.Top + (dirtyRect.Height / 2f);

            for (var i = 0; i < StarCount; i++)
            {
                var centerX = dirtyRect.Left + (float)((StarSize + StarGap) * i) + radius;
                var star = StarPath(centerX, centerY, radius);

                if (i < Filled)
                {
                    canvas.FillColor = Ink;
                    canvas.FillPath(star);
                }
                else
                {
                    // Stroked, not filled at low alpha: an outline reads as "not earned" at 15dp
                    // where a pale solid just reads as a lighter star.
                    canvas.StrokeColor = EmptyInk;
                    canvas.StrokeSize = 1.4f;
                    canvas.StrokeLineJoin = LineJoin.Round;
                    canvas.DrawPath(star);
                }
            }
        }

        /// <summary>
        /// The five-point star, alternating outer and inner vertices from the top. The inner radius
        /// is the pentagram's own ratio rather than a rounder figure — anything fatter loses the
        /// star silhouette at this size.
        /// </summary>
        private static PathF StarPath(float centerX, float centerY, float outerRadius)
        {
            // Inset so the stroked outline of an empty star stays inside the row's bounds.
            const float InnerRatio = 0.4f;
            var outer = outerRadius - 0.7f;
            var inner = outer * InnerRatio;

            var path = new PathF();
            for (var point = 0; point < 10; point++)
            {
                var radius = point % 2 == 0 ? outer : inner;
                // Start at 12 o'clock and walk clockwise: -90° puts the first point at the top.
                var angle = (Math.PI / 5 * point) - (Math.PI / 2);
                var x = centerX + (float)(radius * Math.Cos(angle));
                var y = centerY + (float)(radius * Math.Sin(angle));

                if (point == 0)
                    path.MoveTo(x, y);
                else
                    path.LineTo(x, y);
            }

            path.Close();
            return path;
        }
    }
}
