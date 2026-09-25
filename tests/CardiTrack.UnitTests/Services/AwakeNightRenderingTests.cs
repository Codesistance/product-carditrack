using CardiTrack.Application.DTOs.Common;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Services;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// An awake night is stored as 0 minutes so every average counts it — and "0m" is the one way it
/// must never be written: it reads as a figure missing its digits, or a watch that measured
/// nothing. Everywhere a night is named for a model or a caregiver, it is named as awake.
/// </summary>
public class AwakeNightRenderingTests
{
    private static readonly DateOnly Today = new(2026, 9, 25);

    private static ActivityLog Awake(DateOnly date) =>
        new() { Date = date, SleepMinutes = 0, NightStatus = NightSleepStatus.Awake, Steps = 3100 };

    private static ActivityLog Slept(DateOnly date, int minutes) =>
        new() { Date = date, SleepMinutes = minutes, NightStatus = NightSleepStatus.Slept, Steps = 4200 };

    [Fact]
    public void TheProseRow_NamesTheNightAsAwake()
    {
        var lines = MedicalPromptBlocks.DailyLines([Slept(Today.AddDays(-1), 400), Awake(Today)], 2, Today);

        Assert.Contains($"sleep(night ending that morning)={ReadingFigures.AwakeNight}", lines, StringComparison.Ordinal);
        Assert.DoesNotContain("=0m", lines, StringComparison.Ordinal);
    }

    /// <summary>The figure stays 0 — it is averaged as one — and the row says what the 0 means.
    /// Only on the night it describes: a slept night carries no "night" key at all.</summary>
    [Fact]
    public void TheJsonRow_KeepsTheZero_AndSaysWhatItMeans()
    {
        var json = MedicalPromptBlocks.DailyReadingsJson([Slept(Today.AddDays(-1), 400), Awake(Today)], 2, Today);

        Assert.Contains($"\"night\": \"{ReadingFigures.AwakeNight}\"", json, StringComparison.Ordinal);
        Assert.Equal(1, json.Split("\"night\"").Length - 1);
        Assert.Contains("\"sleep_duration_hours\": 0", json, StringComparison.Ordinal);
    }

    [Fact]
    public void APendingNight_SaysItHasNotArrived()
    {
        var pending = new ActivityLog { Date = Today, NightStatus = NightSleepStatus.Pending };

        var lines = MedicalPromptBlocks.DailyLines([Slept(Today.AddDays(-1), 400), pending], 2, Today);

        Assert.Contains($"sleep(night ending that morning)={ReadingFigures.PendingNight}", lines, StringComparison.Ordinal);
    }

    [Fact]
    public void ChatsCodeWrittenReply_SaysAwake()
    {
        var reply = MemberChatReplies.MetricReadingReply(
            "Moses", StatusMetric.Sleep, [Slept(Today.AddDays(-1), 400), Awake(Today)], Today);

        Assert.Equal(
            "Moses was awake through last night \u2014 the watch was worn all night and recorded no sleep; "
            + "the night before last it was 6h 40m.",
            reply);
    }

    /// <summary>The window summary's lowest night is the awake one, and says so.</summary>
    [Fact]
    public void TheWindowSummary_NamesAnAwakeLowest()
    {
        var week = Enumerable.Range(0, 6).Select(i => Slept(Today.AddDays(-6 + i), 380 + (i * 10))).ToList();
        week.Add(Awake(Today));

        var prompt = MemberChatService.FormatFetchedData(
            new FetchedMemberData { RecentActivity = week, RecentActivityWindow = (Today.AddDays(-6), Today) },
            Today, ageYears: 80, askedMetrics: null);

        Assert.Contains($"\"lowest\": \"Sep 25: {ReadingFigures.AwakeNight}\"", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSleepAlert_NamesAnAwakeNight()
    {
        var finding = StatisticalAlertRules.IrregularSleep(
            new PatternBaseline { AvgSleepMinutes = 400 }, Awake(Today), ageYears: 80);

        Assert.NotNull(finding);
        Assert.Contains(ReadingFigures.AwakeNight, finding.Observation, StringComparison.Ordinal);
    }
}
