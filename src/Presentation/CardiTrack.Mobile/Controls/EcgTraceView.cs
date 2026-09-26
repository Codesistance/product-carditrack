namespace CardiTrack.Mobile.Controls;

/// <summary>
/// The heartbeat trace on the chat bot's screen, drawn so it can sweep: the same blip as
/// <c>icon_chatbot.svg</c>'s trace, repeated and scrolled right to left while <see cref="Running"/>.
/// </summary>
/// <remarks>
/// Drawn rather than an image so the line can move without a frame animation, and clipped to its
/// own bounds so the blip slides in and out of the screen's rounded rectangle instead of past it.
/// </remarks>
internal sealed class EcgTraceView : GraphicsView
{
    private const string LoopName = "ecg-sweep";

    /// <summary>How long one blip takes to cross the screen.</summary>
    private const uint SweepMs = 1600;

    private readonly TraceDrawable _drawable = new();

    public EcgTraceView()
    {
        Drawable = _drawable;
        InputTransparent = true;
    }

    /// <summary>Starts or stops the sweep. Stopped, the trace rests where the static icon draws it.</summary>
    public bool Running
    {
        set
        {
            this.AbortAnimation(LoopName);
            if (value)
            {
                new Animation(v =>
                    {
                        _drawable.Phase = (float)v;
                        Invalidate();
                    })
                    .Commit(this, LoopName, 16, SweepMs, Easing.Linear, repeat: () => true);
            }
            else
            {
                _drawable.Phase = 0;
                Invalidate();
            }
        }
    }

    private sealed class TraceDrawable : IDrawable
    {
        /// <summary>0 to 1: how far through one sweep the trace is.</summary>
        public float Phase { get; set; }

        // The trace from icon_chatbot.svg, as fractions of its screen: flat, a small rise, the
        // spike down and up, a small dip, flat again. x runs 0..1 across one blip period.
        private static readonly (float X, float Y)[] Blip =
        [
            (0.00f, 0.50f), (0.20f, 0.50f), (0.28f, 0.32f), (0.40f, 0.71f),
            (0.49f, 0.40f), (0.54f, 0.50f), (1.00f, 0.50f),
        ];

        public void Draw(ICanvas canvas, RectF dirtyRect)
        {
            if (dirtyRect.Width <= 0 || dirtyRect.Height <= 0)
                return;

            canvas.SaveState();
            canvas.ClipRectangle(dirtyRect);
            canvas.StrokeColor = Colors.White;
            canvas.StrokeSize = Math.Max(1.5f, dirtyRect.Height * 0.09f);
            canvas.StrokeLineCap = LineCap.Round;
            canvas.StrokeLineJoin = LineJoin.Round;

            // Two periods side by side, shifted left by the phase, so one is always crossing.
            var width = dirtyRect.Width;
            var inset = width * 0.12f;
            var period = width - (2 * inset);
            var offset = inset - (Phase * period);
            var path = new PathF();
            for (var p = 0; p < 3; p++)
            {
                var start = offset + (p * period);
                for (var i = 0; i < Blip.Length; i++)
                {
                    var (x, y) = Blip[i];
                    var point = new PointF(start + (x * period), dirtyRect.Top + (y * dirtyRect.Height));
                    if (p == 0 && i == 0)
                        path.MoveTo(point);
                    else
                        path.LineTo(point);
                }
            }
            canvas.DrawPath(path);
            canvas.RestoreState();
        }
    }
}
