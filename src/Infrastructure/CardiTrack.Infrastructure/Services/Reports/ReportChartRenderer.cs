using System.Globalization;
using System.Text;
using CardiTrack.Application.DTOs.Common;
using CardiTrack.Domain.Entities;

namespace CardiTrack.Infrastructure.Services.Reports;

/// <summary>
/// A vector line chart for one metric over the export window: an SVG of the marks alone, and the
/// labels to lay over it. Vector rather than a raster PNG: the export is printed, and a bitmap
/// that looked fine on a phone turns to fuzz on paper.
/// </summary>
/// <remarks>
/// <para>
/// The SVG carries no text. SVG text resolves through the system's font manager, not the font
/// QuestPDF bundles, and the API and Worker images are chiseled Ubuntu with no system fonts at
/// all — a chart labelled inside the SVG would print its axes in whatever fallback Skia found, or
/// in nothing. The labels are returned as positions instead, and the document sets them in the
/// same face as the rest of the page.
/// </para>
/// <para>
/// Missing days are gaps, never zeros — a dropped watch must not read as "did not move". A
/// skipped day starts a new stroke, and the area wash under the line breaks with it.
/// </para>
/// <para>
/// The y-axis starts at a rounded tick below the lowest reading rather than at zero. Resting
/// heart rate moving from 62 to 71 is the whole story of a week, and on a zero-based axis it is
/// a flat line; the ticks say where the axis starts, so the reader is never misled.
/// </para>
/// </remarks>
internal static class ReportChartRenderer
{
    /// <summary>Where the axis lives inside the figure, in points.</summary>
    private const float Left = 40;
    private const float Right = 46;   // room for the end-of-line value label
    private const float Top = 8;
    private const float Bottom = 20;

    private const string Grid = "#E2E8F0";
    private const string Axis = "#CBD5E1";
    private const string Surface = "#FFFFFF";

    /// <summary>The member's usual and the published band. One neutral for both, a step darker
    /// than the grid: they are context the line is read against, and giving them the metric's own
    /// colour would put three things the same colour on one chart.</summary>
    private const string Comparison = "#64748B";

    /// <summary>
    /// How far the comparisons may stretch the readings' own extent before they are refused it,
    /// as a multiple of that extent. Mirrors the app's <c>TrendScale.Headroom</c> — see
    /// <see cref="Fit"/>.
    /// </summary>
    private const double Headroom = 6d;

    /// <summary>Where a label sits relative to its anchor point.</summary>
    internal enum Anchor
    {
        /// <summary>Text starts at the point (a first date, an end-of-line value).</summary>
        Start,
        /// <summary>Text is centred on the point (a middle date).</summary>
        Middle,
        /// <summary>Text ends at the point (a y tick, the last date).</summary>
        End
    }

    /// <param name="Emphasis">The one directly labelled value — the endpoint — set heavier.</param>
    internal sealed record Label(float X, float Y, string Text, Anchor Anchor, bool Emphasis = false);

    /// <summary>
    /// The comparisons a chat reply carried with its series: the member's own learned normal, and
    /// the published typical-adult band, both already in the series' own unit.
    /// </summary>
    internal sealed record Comparisons(double? Baseline, double? ReferenceLow, double? ReferenceHigh);

    /// <param name="Width">The figure's size in points; the SVG's viewBox is the same, so it
    /// draws 1:1 and the labels land where the geometry expects them.</param>
    internal sealed record Chart(string Svg, float Width, float Height, IReadOnlyList<Label> Labels);

    /// <summary>
    /// The chart, or null when there is nothing to plot — no readings, or a window that ends before
    /// it starts.
    /// </summary>
    /// <param name="tickSteps">Axis step sizes to choose from, in the metric's unit; null for the
    /// usual 1–2–5 sequence. Sleep is in minutes and reads in half hours, not in fifties.</param>
    public static Chart? Line(
        IReadOnlyList<ActivityLog> logs,
        Func<ActivityLog, double?> read,
        DateOnly from,
        DateOnly to,
        string color,
        Func<double, string> format,
        float width,
        float height,
        IReadOnlyList<double>? tickSteps = null)
    {
        var days = to.DayNumber - from.DayNumber;
        if (days < 0)
            return null;

        var points = new List<(int Offset, double Value)>();
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

        return Plot(points, from, days, color, format, width, height, tickSteps, comparisons: null);
    }

    /// <summary>
    /// One <see cref="ChartSeries"/> as a chat reply carried it: the same line as
    /// <see cref="Line"/>, over the days the series itself spans, with the comparisons the answer
    /// was read against drawn behind it — the member's own usual as a dashed rule, the published
    /// band as a wash.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The window comes from the series, not from the export's date range: an exported answer has
    /// to show the days it was about, and a transcript's range is the conversation's, which is
    /// usually one afternoon.
    /// </para>
    /// <para>
    /// Null when the series has nothing to plot. A single reading is still drawn here, as the
    /// lone-point mark the renderer already has — unlike the app, which drops it, an exported
    /// document has no tap-to-inspect and no follow-up question, so the one reading the reply
    /// cites is worth a mark.
    /// </para>
    /// </remarks>
    public static Chart? Series(
        ChartSeries series,
        string color,
        Func<double, string> format,
        float width,
        float height,
        IReadOnlyList<double>? tickSteps = null)
    {
        if (series.Points.Count == 0)
            return null;

        // Indexer assignment, and min/max rather than first/last: a stored series with duplicated
        // or unordered dates degrades to a drawable chart rather than a wrong one. Same
        // defensiveness as the app's ChatChartItem.From, for the same reason — this data was
        // written by an older build of the service and is read back months later.
        var byDate = new Dictionary<DateOnly, double>();
        foreach (var point in series.Points)
            byDate[point.Date] = point.Value;

        var from = series.Points.Min(p => p.Date);
        var days = series.Points.Max(p => p.Date).DayNumber - from.DayNumber;

        var points = byDate
            .Select(p => (Offset: p.Key.DayNumber - from.DayNumber, Value: p.Value))
            .ToList();

        return Plot(points, from, days, color, format, width, height, tickSteps,
            new Comparisons(
                series.Baseline,
                series.Reference is { } low ? (double)low.Low : null,
                series.Reference is { } high ? (double)high.High : null));
    }

    /// <summary>The marks themselves, once the caller has resolved which days are being
    /// plotted.</summary>
    /// <param name="comparisons">The member's usual and the published band, or null where the
    /// chart has none — the health-data export's trends plot readings alone.</param>
    private static Chart? Plot(
        List<(int Offset, double Value)> points,
        DateOnly from,
        int days,
        string color,
        Func<double, string> format,
        float width,
        float height,
        IReadOnlyList<double>? tickSteps,
        Comparisons? comparisons)
    {
        if (days < 0 || points.Count == 0)
            return null;

        points.Sort((a, b) => a.Offset.CompareTo(b.Offset));

        var (fitMin, fitMax) = Fit(points.Min(p => p.Value), points.Max(p => p.Value), comparisons);
        var (axisMin, axisMax, step) = NiceAxis(fitMin, fitMax, tickSteps);
        var plotWidth = width - Left - Right;
        var plotHeight = height - Top - Bottom;
        var span = Math.Max(days, 1);
        var baseline = height - Bottom;

        float X(int offset) => Left + (float)offset / span * plotWidth;
        float Y(double value) => Top + (float)((axisMax - value) / (axisMax - axisMin) * plotHeight);

        var labels = new List<Label>();
        var svg = new StringBuilder();
        svg.Append(CultureInfo.InvariantCulture,
            $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width:0.##}\" height=\"{height:0.##}\" viewBox=\"0 0 {width:0.##} {height:0.##}\">");

        // The published band first, under everything: it is context for the line, not a mark of
        // its own. Clipped to the plot rather than allowed to rescale it past what Fit admitted —
        // an area running off an edge reads correctly as continuing beyond the chart.
        if (comparisons is { ReferenceLow: { } bandLow, ReferenceHigh: { } bandHigh }
            && bandHigh > bandLow && bandLow < axisMax && bandHigh > axisMin)
        {
            var bandTop = Y(Math.Min(bandHigh, axisMax));
            var bandBottom = Y(Math.Max(bandLow, axisMin));
            svg.Append(CultureInfo.InvariantCulture,
                $"<rect x=\"{Left}\" y=\"{bandTop:0.##}\" width=\"{width - Right - Left:0.##}\" height=\"{bandBottom - bandTop:0.##}\" fill=\"{Comparison}\" fill-opacity=\"0.10\"/>");
        }

        // Gridlines and y ticks: hairline, solid, one step off the surface, and recessive.
        for (var tick = axisMin; tick <= axisMax + step / 2; tick += step)
        {
            var y = Y(tick);
            svg.Append(CultureInfo.InvariantCulture,
                $"<line x1=\"{Left}\" y1=\"{y:0.##}\" x2=\"{width - Right:0.##}\" y2=\"{y:0.##}\" stroke=\"{Grid}\" stroke-width=\"1\"/>");
            labels.Add(new Label(Left - 6, y, format(tick), Anchor.End));
        }

        // The floor of the plot, slightly darker than the grid so the chart has a baseline.
        svg.Append(CultureInfo.InvariantCulture,
            $"<line x1=\"{Left}\" y1=\"{baseline:0.##}\" x2=\"{width - Right:0.##}\" y2=\"{baseline:0.##}\" stroke=\"{Axis}\" stroke-width=\"1\"/>");

        // X ticks: first day, last day, and a few evenly spaced dates between.
        foreach (var offset in DateTicks(days))
        {
            var anchor = offset == 0 ? Anchor.Start : offset == days ? Anchor.End : Anchor.Middle;
            labels.Add(new Label(
                X(offset), baseline + 4, from.AddDays(offset).ToString("d MMM", CultureInfo.InvariantCulture), anchor));
        }

        // One stroke per run of consecutive days; the wash under each run is a separate shape so
        // it cannot bridge a gap either.
        var runs = new List<List<(int Offset, double Value)>>();
        foreach (var point in points)
        {
            if (runs.Count == 0 || point.Offset > runs[^1][^1].Offset + 1)
                runs.Add([]);
            runs[^1].Add(point);
        }

        foreach (var run in runs)
        {
            if (run.Count == 1)
                continue;

            var line = new StringBuilder();
            foreach (var (offset, value) in run)
                line.Append(CultureInfo.InvariantCulture, $"{(line.Length == 0 ? "M" : "L")}{X(offset):0.##} {Y(value):0.##} ");

            var wash = new StringBuilder(line.ToString());
            wash.Append(CultureInfo.InvariantCulture, $"L{X(run[^1].Offset):0.##} {baseline:0.##} L{X(run[0].Offset):0.##} {baseline:0.##} Z");

            svg.Append(CultureInfo.InvariantCulture, $"<path d=\"{wash}\" fill=\"{color}\" fill-opacity=\"0.10\"/>");
            svg.Append(CultureInfo.InvariantCulture,
                $"<path d=\"{line}\" fill=\"none\" stroke=\"{color}\" stroke-width=\"2\" stroke-linejoin=\"round\" stroke-linecap=\"round\"/>");
        }

        // A day standing alone between gaps still gets a mark, or it would vanish. Every point
        // gets a small dot while the series is sparse enough for them to read as points.
        var dotRadius = points.Count <= 45 ? 2.2f : 0f;
        foreach (var run in runs)
        {
            var radius = run.Count == 1 ? 3f : dotRadius;
            if (radius == 0)
                continue;
            foreach (var (offset, value) in run)
                svg.Append(CultureInfo.InvariantCulture,
                    $"<circle cx=\"{X(offset):0.##}\" cy=\"{Y(value):0.##}\" r=\"{radius}\" fill=\"{color}\"/>");
        }

        // The member's own usual, dashed so it cannot be mistaken for a reading, and only while
        // the axis has room for it: a rule clamped to an edge would read as a usual sitting
        // exactly there. The app makes the same call and lets its legend carry the number
        // instead — see ChatChartItem.BaselineDrawn — and the document does the same.
        if (comparisons?.Baseline is { } usual && usual >= axisMin && usual <= axisMax)
        {
            svg.Append(CultureInfo.InvariantCulture,
                $"<line x1=\"{Left}\" y1=\"{Y(usual):0.##}\" x2=\"{width - Right:0.##}\" y2=\"{Y(usual):0.##}\" stroke=\"{Comparison}\" stroke-width=\"1.2\" stroke-dasharray=\"4 3\"/>");
        }

        // The endpoint is the one value labelled directly: where the reading stands now, with a
        // surface ring so the marker stays legible over the line it ends.
        var last = points[^1];
        svg.Append(CultureInfo.InvariantCulture,
            $"<circle cx=\"{X(last.Offset):0.##}\" cy=\"{Y(last.Value):0.##}\" r=\"4\" fill=\"{color}\" stroke=\"{Surface}\" stroke-width=\"2\"/>");
        labels.Add(new Label(X(last.Offset) + 7, Y(last.Value), format(last.Value), Anchor.Start, Emphasis: true));

        svg.Append("</svg>");
        return new Chart(svg.ToString(), width, height, labels);
    }


    /// <summary>
    /// Widens the readings' extent to take in the comparisons, while each stays inside
    /// <see cref="Headroom"/> of that extent.
    /// </summary>
    /// <remarks>
    /// The same rule, and the same constant, as the app's <c>TrendScale.For</c>, so a chart drawn
    /// in an export sits on the same axis as the one the caregiver saw in the reply. Restated
    /// here rather than shared because that type lives in the MAUI client's core, which this
    /// assembly cannot reference — the same reason <c>ChatMetricFormat</c> restates the API's
    /// sleep figure. Change one, change both.
    /// </remarks>
    private static (double Min, double Max) Fit(
        double dataMin, double dataMax, Comparisons? comparisons)
    {
        var min = Math.Min(dataMin, dataMax);
        var max = Math.Max(dataMin, dataMax);
        if (comparisons is null)
            return (min, max);

        var extent = max - min;

        if (comparisons.Baseline is { } baseline)
            (min, max) = Admit(min, max, Math.Min(baseline, min), Math.Max(baseline, max), extent);

        if (comparisons.ReferenceLow is not null || comparisons.ReferenceHigh is not null)
        {
            var low = comparisons.ReferenceLow is { } l ? Math.Min(l, min) : min;
            var high = comparisons.ReferenceHigh is { } h ? Math.Max(h, max) : max;
            (min, max) = Admit(min, max, low, high, extent);
        }

        return (min, max);
    }

    /// <param name="extent">The readings' own extent. Every candidate is measured against that
    /// rather than against the running total, so a baseline that has already widened the plot
    /// cannot then let in a band that would have been refused on its own.</param>
    private static (double Min, double Max) Admit(
        double min, double max, double candidateMin, double candidateMax, double extent)
    {
        if (extent <= 0)
            return (candidateMin, candidateMax);

        return candidateMax - candidateMin <= extent * Headroom
            ? (candidateMin, candidateMax)
            : (min, max);
    }

    /// <summary>
    /// A rounded axis range with three to five ticks: the floor just below the lowest reading and
    /// the ceiling just above the highest, in 1–2–5 steps unless the metric supplies its own.
    /// </summary>
    internal static (double Min, double Max, double Step) NiceAxis(
        double min, double max, IReadOnlyList<double>? steps = null)
    {
        // Held before the flat-series adjustment below moves it: the zero clamp at the end asks
        // whether the *readings* were ever negative, not whether the padding went below zero.
        var lowestReading = min;

        if (max - min < 1e-9)
        {
            // A flat series still needs a range to sit in; a few percent either side keeps it
            // centred, in ticks that stay whole for an integer metric.
            var air = Math.Max(1, Math.Abs(min) * 0.05);
            min -= air;
            max += air;
        }

        var rough = (max - min) / 4;
        double step;
        if (steps is { Count: > 0 })
        {
            step = steps.FirstOrDefault(s => s >= rough, steps[^1]);
        }
        else
        {
            var magnitude = Math.Pow(10, Math.Floor(Math.Log10(rough)));
            step = (rough / magnitude) switch
            {
                <= 1 => 1,
                <= 2 => 2,
                <= 5 => 5,
                _ => 10
            } * magnitude;
        }

        var axisMin = Math.Floor(min / step) * step;
        var axisMax = Math.Ceiling(max / step) * step;

        // A reading on the frame reads as clipped; give the extremes a little air.
        if (min - axisMin < step * 0.1)
            axisMin -= step;
        if (axisMax - max < step * 0.1)
            axisMax += step;

        // Steps, minutes and percentages have no negative side; a floor below zero is a lie. It
        // matters most for the case that looks least interesting — a member flat at zero steps
        // all window, whose chart would otherwise tick down to -2,000.
        if (axisMin < 0 && lowestReading >= 0)
            axisMin = 0;

        return (axisMin, axisMax, step);
    }

    private static IEnumerable<int> DateTicks(int days)
    {
        if (days == 0)
        {
            yield return 0;
            yield break;
        }

        var count = Math.Min(6, days + 1);
        var seen = new HashSet<int>();
        for (var i = 0; i < count; i++)
        {
            var offset = (int)Math.Round((double)days * i / (count - 1));
            if (seen.Add(offset))
                yield return offset;
        }
    }
}
