namespace CardiTrack.Mobile.Controls;

/// <summary>
/// A small ring filled clockwise from twelve o'clock to <see cref="Progress"/> — "Complete the
/// picture"'s per-member set-up progress. Drawn rather than composed from shapes: an arc of an
/// arbitrary sweep is one call on a canvas and has no equivalent in MAUI's shape primitives.
/// </summary>
public sealed class ProgressRing : GraphicsView
{
    private readonly RingDrawable _ring = new();

    public ProgressRing()
    {
        Drawable = _ring;
        WidthRequest = 40;
        HeightRequest = 40;
        InputTransparent = true;
        var resources = Microsoft.Maui.Controls.Application.Current!.Resources;
        _ring.Track = (Color)resources["InputBackground"];
        _ring.Fill = (Color)resources["StatusGreen"];
    }

    /// <summary>0 to 1.</summary>
    public double Progress
    {
        get => _ring.Progress;
        set
        {
            _ring.Progress = Math.Clamp(value, 0, 1);
            Invalidate();
        }
    }

    private sealed class RingDrawable : IDrawable
    {
        public double Progress { get; set; }
        public Color Track { get; set; } = Colors.LightGray;
        public Color Fill { get; set; } = Colors.Green;

        public void Draw(ICanvas canvas, RectF dirtyRect)
        {
            const float stroke = 4.5f;
            var size = Math.Min(dirtyRect.Width, dirtyRect.Height) - stroke;
            var x = dirtyRect.Center.X - size / 2;
            var y = dirtyRect.Center.Y - size / 2;

            canvas.StrokeSize = stroke;
            canvas.StrokeLineCap = LineCap.Round;

            canvas.StrokeColor = Track;
            canvas.DrawEllipse(x, y, size, size);

            if (Progress <= 0)
                return;

            canvas.StrokeColor = Fill;
            if (Progress >= 1)
            {
                canvas.DrawEllipse(x, y, size, size);
                return;
            }

            // Angles run anticlockwise from three o'clock; clockwise from twelve is 90 down to
            // 90 - sweep.
            var sweep = (float)(360 * Progress);
            canvas.DrawArc(x, y, size, size, 90, 90 - sweep, clockwise: true, closed: false);
        }
    }
}
