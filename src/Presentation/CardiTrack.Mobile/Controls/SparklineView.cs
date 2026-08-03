namespace CardiTrack.Mobile.Controls;

/// <summary>
/// Minimal 7-point sparkline drawn with GraphicsView — no charting dependency.
/// Null points are skipped (gap days).
/// </summary>
public sealed class SparklineView : GraphicsView
{
    public static readonly BindableProperty PointsProperty = BindableProperty.Create(
        nameof(Points), typeof(IList<decimal?>), typeof(SparklineView),
        propertyChanged: (b, _, _) => ((SparklineView)b).Invalidate());

    public static readonly BindableProperty LineColorProperty = BindableProperty.Create(
        nameof(LineColor), typeof(Color), typeof(SparklineView), Colors.SteelBlue,
        propertyChanged: (b, _, _) => ((SparklineView)b).Invalidate());

    public IList<decimal?>? Points
    {
        get => (IList<decimal?>?)GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    public Color LineColor
    {
        get => (Color)GetValue(LineColorProperty);
        set => SetValue(LineColorProperty, value);
    }

    public SparklineView()
    {
        Drawable = new SparklineDrawable(this);
        HeightRequest = 28;
    }

    private sealed class SparklineDrawable : IDrawable
    {
        private readonly SparklineView _view;

        public SparklineDrawable(SparklineView view) => _view = view;

        public void Draw(ICanvas canvas, RectF bounds)
        {
            var points = _view.Points;
            if (points is null || points.Count < 2)
                return;

            var values = points.Where(p => p.HasValue).Select(p => (float)p!.Value).ToList();
            if (values.Count < 2)
                return;

            var min = values.Min();
            var max = values.Max();
            var range = max - min < 0.0001f ? 1f : max - min;

            const float pad = 2f;
            var stepX = (bounds.Width - pad * 2) / (points.Count - 1);

            var path = new PathF();
            var started = false;
            for (var i = 0; i < points.Count; i++)
            {
                if (points[i] is not { } value)
                {
                    started = false;
                    continue;
                }
                var x = bounds.Left + pad + stepX * i;
                var y = bounds.Bottom - pad - ((float)value - min) / range * (bounds.Height - pad * 2);
                if (!started)
                {
                    path.MoveTo(x, y);
                    started = true;
                }
                else
                {
                    path.LineTo(x, y);
                }
            }

            canvas.StrokeColor = _view.LineColor;
            canvas.StrokeSize = 2f;
            canvas.StrokeLineCap = LineCap.Round;
            canvas.StrokeLineJoin = LineJoin.Round;
            canvas.DrawPath(path);
        }
    }
}
