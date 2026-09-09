using CardiTrack.Domain.Entities;
using SkiaSharp;

namespace CardiTrack.Infrastructure.Services.Reports;

/// <summary>
/// A small line chart for one metric over the export window. Missing days are
/// gaps, never zeros — a dropped watch must not read as "did not move".
/// </summary>
internal static class ReportChartRenderer
{
    private const int Width = 520;
    private const int Height = 160;
    private const int Left = 36;
    private const int Right = 12;
    private const int Top = 16;
    private const int Bottom = 24;

    public static byte[]? Line(
        IReadOnlyList<ActivityLog> logs,
        Func<ActivityLog, double?> read,
        DateOnly from,
        DateOnly to)
    {
        var points = new List<(int Offset, double Value)>();
        var days = to.DayNumber - from.DayNumber;
        if (days < 0)
            return null;

        foreach (var log in logs)
        {
            var value = read(log);
            if (value is null)
                continue;
            var offset = log.Date.DayNumber - from.DayNumber;
            if (offset < 0 || offset > days)
                continue;
            points.Add((offset, value.Value));
        }

        if (points.Count == 0)
            return null;

        var min = points.Min(p => p.Value);
        var max = points.Max(p => p.Value);
        if (Math.Abs(max - min) < 0.0001)
        {
            min -= 1;
            max += 1;
        }

        using var surface = SKSurface.Create(new SKImageInfo(Width, Height));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);

        using var axis = new SKPaint { Color = new SKColor(0xCC, 0xCC, 0xCC), StrokeWidth = 1, IsAntialias = true };
        canvas.DrawLine(Left, Height - Bottom, Width - Right, Height - Bottom, axis);
        canvas.DrawLine(Left, Top, Left, Height - Bottom, axis);

        using var line = new SKPaint
        {
            Color = new SKColor(0x17, 0x4E, 0x86),
            StrokeWidth = 2,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke
        };
        var builder = new SKPathBuilder();
        var chartWidth = Width - Left - Right;
        var chartHeight = Height - Top - Bottom;
        var span = Math.Max(days, 1);

        var lastOffset = int.MinValue;
        foreach (var (offset, value) in points.OrderBy(p => p.Offset))
        {
            var x = Left + (float)offset / span * chartWidth;
            var y = Top + (float)((max - value) / (max - min) * chartHeight);
            // A skipped day starts a new stroke — connecting across it would draw
            // a line through a day the device never reported.
            if (lastOffset == int.MinValue || offset > lastOffset + 1)
                builder.MoveTo(x, y);
            else
                builder.LineTo(x, y);
            lastOffset = offset;
        }

        using var path = builder.Detach();
        canvas.DrawPath(path, line);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        return data.ToArray();
    }
}
