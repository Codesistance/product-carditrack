using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Infrastructure.Services;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// The dashboard hero's two-day window renderer: completed overnight readings before running
/// totals, no empty <c>steps=</c> holes, and no row older than yesterday.
/// </summary>
public class StatusWindowDailyLinesTests
{
    private static readonly DateOnly Today = new(2026, 8, 22);
    private static readonly DateOnly Yesterday = Today.AddDays(-1);

    private static DigestDayProgress MorningProgress() =>
        DigestDayProgress.For(Today.ToDateTime(new TimeOnly(8, 0)), baseline: null);

    [Fact]
    public void OvernightFiguresPrecedeSteps()
    {
        var lines = MedicalPromptBlocks.StatusWindowDailyLines(
            [
                new ActivityLog
                {
                    Date = Yesterday,
                    Steps = 6100,
                    SleepMinutes = 420,
                    HeartRateVariabilityMs = 41.2m,
                    RestingHeartRate = 71,
                },
            ],
            Today,
            MorningProgress());

        var yesterdayLine = Assert.Single(lines.Split('\n'), l => l.Contains("Yesterday", StringComparison.Ordinal));
        Assert.True(
            yesterdayLine.IndexOf("HRV=41.2ms", StringComparison.Ordinal)
            < yesterdayLine.IndexOf("steps=6100", StringComparison.Ordinal));
        Assert.True(
            yesterdayLine.IndexOf("sleep(night ending that morning)=420min", StringComparison.Ordinal)
            < yesterdayLine.IndexOf("HR=71", StringComparison.Ordinal));
    }

    [Fact]
    public void AMissingColumnIsOmitted_NotAnEmptyEquals()
    {
        var lines = MedicalPromptBlocks.StatusWindowDailyLines(
            [new ActivityLog { Date = Today, RestingHeartRate = 70 }],
            Today,
            MorningProgress());

        Assert.DoesNotContain("steps=", lines);
        Assert.DoesNotContain("HR=,", lines);
        Assert.Contains("HR=70", lines);
    }

    [Fact]
    public void ARowOlderThanYesterday_IsDropped()
    {
        var lines = MedicalPromptBlocks.StatusWindowDailyLines(
            [
                new ActivityLog { Date = Today.AddDays(-2), Steps = 8888 },
                new ActivityLog { Date = Yesterday, Steps = 6100 },
                new ActivityLog { Date = Today, Steps = 900 },
            ],
            Today,
            MorningProgress());

        Assert.DoesNotContain("8888", lines);
        Assert.DoesNotContain("days ago", lines);
        Assert.Contains("steps=6100", lines);
        Assert.Contains("steps=900", lines);
    }

    [Fact]
    public void AMissingTodayRow_IsAnchoredSoYesterdayIsNotReadAsLastNight()
    {
        var lines = MedicalPromptBlocks.StatusWindowDailyLines(
            [new ActivityLog { Date = Yesterday, SleepMinutes = 400, Steps = 2800 }],
            Today,
            MorningProgress());

        Assert.Contains("Yesterday", lines);
        Assert.Contains("Today so far", lines);
        Assert.Contains("nothing measured", lines);
    }

    [Fact]
    public void NoRows_SaysSo()
    {
        Assert.Equal(
            "No recent activity data.",
            MedicalPromptBlocks.StatusWindowDailyLines([], Today, MorningProgress()));
    }
}
