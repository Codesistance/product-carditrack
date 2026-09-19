using System.Globalization;
using CardiTrack.Application.DTOs.Common;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Infrastructure.Services.Reports;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// The charts a chat reply carried, drawn into an export: the graphs are the whole reason a
/// transcript is worth printing rather than copying out of the app, and they are the part a
/// caregiver would not notice was missing until the appointment.
/// </summary>
public class ChatTranscriptChartTests
{
    private const float Width = 481;
    private const float Height = 120;

    private static ChartSeries Steps(params (int Day, double Value)[] points) =>
        new("Steps",
            points.Select(p => new ChartPoint(new DateOnly(2026, 2, p.Day), p.Value)).ToList());

    private static ReportChartRenderer.Chart? Draw(
        ChartSeries series, IReadOnlyList<double>? tickSteps = null)
    {
        var style = ChatChartStyle.For(series.Metric);
        return ReportChartRenderer.Series(
            series, style.Color, style.Format, Width, Height, tickSteps ?? style.TickSteps);
    }

    [Fact]
    public void Series_DrawsTheDaysTheReplyCarried_NotTheExportsRange()
    {
        // The window is the series', not the request's: a conversation exported weeks later still
        // has to show the days its answer was about.
        var chart = Draw(Steps((7, 4000), (8, 4200), (9, 3100)));

        Assert.NotNull(chart);
        Assert.StartsWith("<svg", chart!.Svg);
        Assert.Contains(chart.Labels, l => l.Text == "7 Feb");
        Assert.Contains(chart.Labels, l => l.Text == "9 Feb");
    }

    [Fact]
    public void Series_CarriesNoTextInsideTheSvg()
    {
        // Same reason as the health export's charts: the production images have no system fonts,
        // so every label comes back as a position the document sets in its own face.
        var chart = Draw(Steps((7, 4000), (8, 4200)));

        Assert.DoesNotContain("<text", chart!.Svg);
        Assert.NotEmpty(chart.Labels);
    }

    [Fact]
    public void Series_DrawsOneReading()
    {
        // The app drops a single-point series and falls back to a text summary — it has a tap
        // callout and a follow-up question to offer instead. A printed page has neither, so the
        // one reading the reply cites is still worth a mark.
        var chart = Draw(Steps((7, 4000)));

        Assert.NotNull(chart);
        Assert.Contains("<circle", chart!.Svg);
    }

    [Fact]
    public void Series_ReturnsNothing_WhenThereIsNothingToPlot()
    {
        Assert.Null(Draw(new ChartSeries("Steps", [])));
    }

    [Fact]
    public void Series_DrawsTheMembersUsualAsADashedRule()
    {
        var withBaseline = new ChartSeries(
            "Resting heart rate",
            [new ChartPoint(new DateOnly(2026, 2, 7), 62), new ChartPoint(new DateOnly(2026, 2, 8), 71)],
            Baseline: 64);

        var chart = Draw(withBaseline);

        Assert.Contains("stroke-dasharray", chart!.Svg);
    }

    [Fact]
    public void Series_DrawsNoRule_WhenTheReplyCarriedNoBaseline()
    {
        Assert.DoesNotContain("stroke-dasharray", Draw(Steps((7, 4000), (8, 4200)))!.Svg);
    }

    [Fact]
    public void Series_ShadesThePublishedBandBehindTheLine()
    {
        var withBand = new ChartSeries(
            "Resting heart rate",
            [new ChartPoint(new DateOnly(2026, 2, 7), 62), new ChartPoint(new DateOnly(2026, 2, 8), 71)],
            Reference: new MetricReference { Low = 60, High = 100, Source = "AHA" });

        Assert.Contains("<rect", Draw(withBand)!.Svg);
    }

    [Fact]
    public void Series_RefusesABandThatWouldFlattenTheReadings()
    {
        // TrendScale's rule, restated server-side: a member whose heart rate held inside two
        // beats all week would be a straight line under a 60–100 band, which trades the thing
        // the caregiver came to see for context the legend already carries in words.
        var flat = new ChartSeries(
            "Resting heart rate",
            [new ChartPoint(new DateOnly(2026, 2, 7), 71), new ChartPoint(new DateOnly(2026, 2, 8), 72)],
            Reference: new MetricReference { Low = 60, High = 100, Source = "AHA" });

        var chart = Draw(flat);

        // The readings still set the axis, so both of them are labelled inside it.
        Assert.Contains(chart!.Labels, l => l.Text == "72");
        Assert.DoesNotContain(chart.Labels, l => l.Text == "100");
    }

    [Fact]
    public void Series_SurvivesDuplicatedAndUnorderedDates()
    {
        // Stored series are read back months after the build that wrote them. A malformed one
        // degrades to a drawable chart rather than throwing away the export.
        var messy = new ChartSeries("Steps",
        [
            new ChartPoint(new DateOnly(2026, 2, 9), 3100),
            new ChartPoint(new DateOnly(2026, 2, 7), 4000),
            new ChartPoint(new DateOnly(2026, 2, 7), 4050),
        ]);

        var chart = Draw(messy);

        Assert.NotNull(chart);
        Assert.Contains(chart!.Labels, l => l.Text == "7 Feb");
    }

    [Fact]
    public void Style_SpellsSleepInHoursAndMinutes()
    {
        // Sleep is stored in minutes and read in half hours. "465" beside a chart is a number no
        // caregiver thinks in — and the app says the same night the same way.
        var style = ChatChartStyle.For("Sleep");

        Assert.Equal("7h 45m", style.Format(465));
        Assert.Equal("8h", style.Format(480));
    }

    [Fact]
    public void Style_GivesAnUnknownSeriesAColourAndAFormat()
    {
        // A transcript is read back long after it was written, and a series added to the chat
        // after this build must still draw rather than crash the export.
        var style = ChatChartStyle.For("Something we have not shipped yet");

        Assert.False(string.IsNullOrWhiteSpace(style.Color));
        Assert.Equal("12.5", style.Format(12.5).Replace(",", ".", StringComparison.Ordinal));
        Assert.Equal(string.Empty, style.Unit);
    }

    [Fact]
    public void Style_KeepsTheAppsIdentityInks()
    {
        // The same line has to be the same colour in the reply and in the copy of it.
        Assert.Equal("#1884DC", ChatChartStyle.For("Steps").Color);
        Assert.Equal("#E53E3E", ChatChartStyle.For("Resting heart rate").Color);
        Assert.Equal("#7C6FDC", ChatChartStyle.For("Sleep").Color);
    }

    [Fact]
    public void Style_FormatsWholeMetricsWithoutADecimal()
    {
        Assert.Equal(
            4_000.ToString("N0", CultureInfo.InvariantCulture),
            ChatChartStyle.For("Steps").Format(4000));
    }
}
