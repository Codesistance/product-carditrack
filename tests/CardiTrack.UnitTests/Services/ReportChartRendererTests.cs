using CardiTrack.Domain.Entities;
using CardiTrack.Infrastructure.Services.Reports;

namespace CardiTrack.UnitTests.Services;

public class ReportChartRendererTests
{
    private const float Width = 493;
    private const float Height = 130;

    private static ReportChartRenderer.Chart? Steps(
        IReadOnlyList<ActivityLog> logs, DateOnly from, DateOnly to) =>
        ReportChartRenderer.Line(
            logs, log => log.Steps, from, to, "#1884DC", v => v.ToString("N0"), Width, Height);

    [Fact]
    public void Line_ReturnsNothing_WhenEveryReadingIsMissing()
    {
        var logs = new[]
        {
            new ActivityLog { Date = new DateOnly(2026, 2, 7), Steps = null },
            new ActivityLog { Date = new DateOnly(2026, 2, 8), Steps = null }
        };

        Assert.Null(Steps(logs, new DateOnly(2026, 2, 7), new DateOnly(2026, 2, 8)));
    }

    [Fact]
    public void Line_ReturnsAVectorFigure_WhenAtLeastOneDayHasAValue()
    {
        var logs = new[]
        {
            new ActivityLog { Date = new DateOnly(2026, 2, 7), Steps = 4000 },
            new ActivityLog { Date = new DateOnly(2026, 2, 8), Steps = null },
            new ActivityLog { Date = new DateOnly(2026, 2, 9), Steps = 4200 }
        };

        var chart = Steps(logs, new DateOnly(2026, 2, 7), new DateOnly(2026, 2, 9));

        Assert.NotNull(chart);
        Assert.StartsWith("<svg", chart!.Svg);
        Assert.Equal(Width, chart.Width);
        Assert.Equal(Height, chart.Height);
    }

    [Fact]
    public void Line_CarriesNoTextInsideTheSvg()
    {
        // SVG text resolves through the system font manager, and the production images have no
        // system fonts. Every label must come back as a position for the document to set itself.
        var chart = Steps(
            [new ActivityLog { Date = new DateOnly(2026, 2, 7), Steps = 4000 },
             new ActivityLog { Date = new DateOnly(2026, 2, 8), Steps = 4100 }],
            new DateOnly(2026, 2, 7), new DateOnly(2026, 2, 8));

        Assert.DoesNotContain("<text", chart!.Svg);
        Assert.Contains(chart.Labels, l => l.Text == "7 Feb");
        Assert.Contains(chart.Labels, l => l.Text == "8 Feb");
    }

    [Fact]
    public void Line_LabelsTheEndpointDirectly_AndNothingElse()
    {
        var chart = Steps(
            [new ActivityLog { Date = new DateOnly(2026, 2, 7), Steps = 4000 },
             new ActivityLog { Date = new DateOnly(2026, 2, 8), Steps = 4100 },
             new ActivityLog { Date = new DateOnly(2026, 2, 9), Steps = 4350 }],
            new DateOnly(2026, 2, 7), new DateOnly(2026, 2, 9));

        var emphasised = Assert.Single(chart!.Labels, l => l.Emphasis);
        Assert.Equal("4,350", emphasised.Text);
        Assert.Equal(ReportChartRenderer.Anchor.Start, emphasised.Anchor);
    }

    [Fact]
    public void Line_DoesNotInventAZero_ForAMissingDay()
    {
        // A day with no reading must not become a plotted 0. One real point still
        // renders; a fabricated zero would be a second point and a different picture.
        var onlyReal = Steps(
            [new ActivityLog { Date = new DateOnly(2026, 2, 7), Steps = 4000 }],
            new DateOnly(2026, 2, 7), new DateOnly(2026, 2, 9));
        var withGap = Steps(
            [
                new ActivityLog { Date = new DateOnly(2026, 2, 7), Steps = 4000 },
                new ActivityLog { Date = new DateOnly(2026, 2, 8), Steps = null }
            ],
            new DateOnly(2026, 2, 7), new DateOnly(2026, 2, 9));

        Assert.NotNull(onlyReal);
        Assert.Equal(onlyReal!.Svg, withGap!.Svg);
    }

    [Fact]
    public void Line_BreaksTheStrokeAcrossASkippedDay()
    {
        // Two runs of readings around a day the watch was off draw as two strokes, not one line
        // drawn straight through a day the device never reported.
        var chart = Steps(
            [
                new ActivityLog { Date = new DateOnly(2026, 2, 7), Steps = 4000 },
                new ActivityLog { Date = new DateOnly(2026, 2, 8), Steps = 4100 },
                new ActivityLog { Date = new DateOnly(2026, 2, 10), Steps = 4300 },
                new ActivityLog { Date = new DateOnly(2026, 2, 11), Steps = 4400 }
            ],
            new DateOnly(2026, 2, 7), new DateOnly(2026, 2, 11));

        var strokes = chart!.Svg.Split("fill=\"none\"").Length - 1;
        Assert.Equal(2, strokes);
    }

    [Theory]
    [InlineData(1344, 6177, 0, 8000, 2000)]      // steps: a 1-2-5 step, floor clamped at zero
    [InlineData(63, 75, 60, 80, 5)]              // resting heart rate
    [InlineData(95.0, 97.6, 94, 98, 1)]          // SpO2: the low reading no longer sits on the frame
    [InlineData(70, 70, 66, 74, 2)]              // a flat series still gets a range
    public void NiceAxis_RoundsToCleanTicks_WithAirAtBothEnds(
        double min, double max, double axisMin, double axisMax, double step)
    {
        var axis = ReportChartRenderer.NiceAxis(min, max);

        Assert.Equal(axisMin, axis.Min);
        Assert.Equal(axisMax, axis.Max);
        Assert.Equal(step, axis.Step);
    }

    [Fact]
    public void NiceAxis_UsesTheMetricsOwnSteps_WhenGiven()
    {
        // Sleep is in minutes: 1-2-5 would tick every 50 minutes, which nobody reads. In the
        // metric's own steps a 336–459 minute night ticks by the hour.
        var axis = ReportChartRenderer.NiceAxis(336, 459, [15, 30, 60, 120, 240]);

        Assert.Equal(60, axis.Step);
        Assert.Equal(300, axis.Min);
        Assert.Equal(480, axis.Max);
    }
}
