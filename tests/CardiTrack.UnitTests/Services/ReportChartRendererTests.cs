using CardiTrack.Domain.Entities;
using CardiTrack.Infrastructure.Services.Reports;

namespace CardiTrack.UnitTests.Services;

public class ReportChartRendererTests
{
    [Fact]
    public void Line_ReturnsNothing_WhenEveryReadingIsMissing()
    {
        var logs = new[]
        {
            new ActivityLog { Date = new DateOnly(2026, 2, 7), Steps = null },
            new ActivityLog { Date = new DateOnly(2026, 2, 8), Steps = null }
        };

        Assert.Null(ReportChartRenderer.Line(
            logs, log => log.Steps, new DateOnly(2026, 2, 7), new DateOnly(2026, 2, 8)));
    }

    [Fact]
    public void Line_ReturnsAPng_WhenAtLeastOneDayHasAValue()
    {
        var logs = new[]
        {
            new ActivityLog { Date = new DateOnly(2026, 2, 7), Steps = 4000 },
            new ActivityLog { Date = new DateOnly(2026, 2, 8), Steps = null },
            new ActivityLog { Date = new DateOnly(2026, 2, 9), Steps = 4200 }
        };

        var png = ReportChartRenderer.Line(
            logs, log => log.Steps, new DateOnly(2026, 2, 7), new DateOnly(2026, 2, 9));

        Assert.NotNull(png);
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], png!.Take(4));
    }

    [Fact]
    public void Line_DoesNotInventAZero_ForAMissingDay()
    {
        // A day with no reading must not become a plotted 0. One real point still
        // renders; a fabricated zero would be a second point and a different picture.
        var onlyReal = ReportChartRenderer.Line(
            [new ActivityLog { Date = new DateOnly(2026, 2, 7), Steps = 4000 }],
            log => log.Steps, new DateOnly(2026, 2, 7), new DateOnly(2026, 2, 9));
        var withGap = ReportChartRenderer.Line(
            [
                new ActivityLog { Date = new DateOnly(2026, 2, 7), Steps = 4000 },
                new ActivityLog { Date = new DateOnly(2026, 2, 8), Steps = null }
            ],
            log => log.Steps, new DateOnly(2026, 2, 7), new DateOnly(2026, 2, 9));

        Assert.NotNull(onlyReal);
        Assert.Equal(onlyReal!.Length, withGap!.Length);
    }
}
