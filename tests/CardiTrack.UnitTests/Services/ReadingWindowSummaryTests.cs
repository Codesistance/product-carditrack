using CardiTrack.Application.DTOs.Common;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Infrastructure.Services;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// The window arithmetic member chat now does in code, pinned against the week that prompted it:
/// on 2026-09-25 "how did he sleep this week?" was answered with an average of about 2h 22m a
/// night, under a chart of nights running 4h 18m to 7h, because the clinical model had been handed
/// seven rows and left to average them itself.
/// </summary>
public class ReadingWindowSummaryTests
{
    private static readonly DateOnly Today = new(2026, 9, 25);
    private static readonly (DateOnly From, DateOnly To) Week = (Today.AddDays(-6), Today);

    /// <summary>
    /// A week the shape of the reported one — a near-empty first night, nights falling from about
    /// seven hours to under four, last night on today's row — with invented figures: committed test
    /// data is a public record, and a real member's readings are health data. Steps on every day
    /// but today, which is still accumulating.
    /// </summary>
    private static List<ActivityLog> ReportedWeek() =>
    [
        new() { Date = Today.AddDays(-6), SleepMinutes = 10, Steps = 4100 },
        new() { Date = Today.AddDays(-5), SleepMinutes = 430, Steps = 5200 },
        new() { Date = Today.AddDays(-4), SleepMinutes = 400, Steps = 4800 },
        new() { Date = Today.AddDays(-3), SleepMinutes = 350, Steps = 3900 },
        new() { Date = Today.AddDays(-2), SleepMinutes = 340, Steps = 4400 },
        new() { Date = Today.AddDays(-1), SleepMinutes = 260, Steps = 3600 },
        new() { Date = Today, SleepMinutes = 225, Steps = 900 },
    ];

    private static PatternBaseline Usual() => new() { AvgSleepMinutes = 345, AvgSteps = 4500 };

    [Fact]
    public void TheWeeksSleepAverage_IsComputedOverEveryNight_IncludingLastNight()
    {
        var sleep = ReadingWindowSummaries.ForMetric(
            ChartMetricKind.Sleep, ReportedWeek(), Week, Today, Usual(), ageYears: 82);

        Assert.NotNull(sleep);
        Assert.Equal(7, sleep.DaysConsidered);
        Assert.Equal(7, sleep.Readings.Count);
        Assert.True(sleep.IsCovered);
        // 2015 minutes over seven nights — about 4h 48m, nowhere near 2h 22m.
        Assert.Equal(2015m / 7, sleep.Average);
        Assert.Equal((Today.AddDays(-6), 10m), sleep.Lowest);
        Assert.Equal((Today.AddDays(-5), 430m), sleep.Highest);
    }

    /// <summary>Today's steps are a day still in progress; averaging 900 in would read as a
    /// collapse the member is not having.</summary>
    [Fact]
    public void ADaytimeTotal_LeavesTodayOut()
    {
        var steps = ReadingWindowSummaries.ForMetric(
            ChartMetricKind.Steps, ReportedWeek(), Week, Today, Usual(), ageYears: 82);

        Assert.NotNull(steps);
        Assert.Equal(6, steps.DaysConsidered);
        Assert.DoesNotContain(steps.Readings, r => r.Day == Today);
        Assert.Equal(26000m / 6, steps.Average);
    }

    [Theory]
    [InlineData(7, 4)]
    [InlineData(6, 4)]
    [InlineData(3, 2)]
    [InlineData(2, 2)]
    public void TheCoverageBar_IsFourOfSeven_RoundedUp(int considered, int required) =>
        Assert.Equal(required, ReadingWindowSummaries.RequiredDays(considered));

    /// <summary>Three nights of seven is not a week, and an average of them would speak for the
    /// four that never arrived.</summary>
    [Fact]
    public void TooFewNights_CarryNoAverage()
    {
        var rows = ReportedWeek().Where(l => l.Date >= Today.AddDays(-2)).ToList();

        var sleep = ReadingWindowSummaries.ForMetric(
            ChartMetricKind.Sleep, rows, Week, Today, Usual(), ageYears: 82);

        Assert.NotNull(sleep);
        Assert.Equal(3, sleep.Readings.Count);
        Assert.False(sleep.IsCovered);
        Assert.Null(sleep.Average);
    }

    /// <summary>A single day has nothing to average across — that question is about a day.</summary>
    [Fact]
    public void ASingleDayWindow_HasNoSummary() =>
        Assert.Null(ReadingWindowSummaries.ForMetric(
            ChartMetricKind.Sleep, ReportedWeek(), (Today, Today), Today, Usual(), ageYears: 82));

    /// <summary>
    /// What the clinical read is now shown: the average, both yardsticks, and the distance to each
    /// already subtracted, in the hours-and-minutes the reply will quote.
    /// </summary>
    [Fact]
    public void ThePrompt_CarriesTheComputedAverage_AndBothComparisons()
    {
        var data = new FetchedMemberData
        {
            RecentActivity = ReportedWeek(),
            RecentActivityWindow = Week,
            Baseline = Usual(),
        };

        var prompt = MemberChatService.FormatFetchedData(data, Today, ageYears: 82, askedMetrics: null);

        Assert.Contains("\"reading\": \"sleep\"", prompt, StringComparison.Ordinal);
        Assert.Contains("\"nights_with_reading\": \"7 of 7\"", prompt, StringComparison.Ordinal);
        Assert.Contains("\"average\": \"4h 48m\"", prompt, StringComparison.Ordinal);
        Assert.Contains("\"published_range\": \"7h to 8h (NSF)\"", prompt, StringComparison.Ordinal);
        Assert.Contains("\"average_vs_published_range\": \"2h 12m below its 7h floor\"", prompt, StringComparison.Ordinal);
        Assert.Contains("\"usual_for_this_member\": \"5h 45m\"", prompt, StringComparison.Ordinal);
        Assert.Contains("\"average_vs_usual\": \"57m below it\"", prompt, StringComparison.Ordinal);
        Assert.Contains("never add up or average the daily readings yourself", prompt, StringComparison.Ordinal);
    }

    /// <summary>A reading short of the bar is written with no average and a reason, rather than
    /// left out — an absent field is one the model fills in.</summary>
    [Fact]
    public void ThePrompt_WritesAShortReadingWithNoAverage()
    {
        var data = new FetchedMemberData
        {
            RecentActivity = ReportedWeek().Where(l => l.Date >= Today.AddDays(-2)).ToList(),
            RecentActivityWindow = Week,
            Baseline = Usual(),
        };

        var prompt = MemberChatService.FormatFetchedData(data, Today, ageYears: 82, askedMetrics: null);

        Assert.Contains("\"average\": null", prompt, StringComparison.Ordinal);
        Assert.Contains("only 3 of 7 nights carried a reading, and 4 are needed", prompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// A reading the question asked about that no day carried is still written, as missing — left
    /// out, the model answers it from the baseline. One nobody asked about stays out, as the daily
    /// rows leave out what the device does not measure.
    /// </summary>
    [Fact]
    public void ThePrompt_StatesAnAskedReadingThatNeverArrived()
    {
        var data = new FetchedMemberData
        {
            RecentActivity = ReportedWeek().Select(l => new ActivityLog { Date = l.Date, Steps = l.Steps }).ToList(),
            RecentActivityWindow = Week,
            Baseline = Usual(),
        };

        var asked = MemberChatService.FormatFetchedData(
            data, Today, ageYears: 82, askedMetrics: [ChartMetricKind.Steps, ChartMetricKind.Sleep]);
        var unasked = MemberChatService.FormatFetchedData(
            data, Today, ageYears: 82, askedMetrics: [ChartMetricKind.Steps]);

        Assert.Contains("\"nights_with_reading\": \"0 of 7\"", asked, StringComparison.Ordinal);
        Assert.DoesNotContain("\"reading\": \"sleep\"", unasked, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same with no rows at all: the investigation rung has no coverage gate, so a question
    /// about sleep over an empty window still has to be told the nights never arrived.
    /// </summary>
    [Fact]
    public void ThePrompt_StatesAnAskedReading_EvenWhenTheWindowHasNoRows()
    {
        var data = new FetchedMemberData
        {
            RecentActivity = [],
            RecentActivityWindow = Week,
            Baseline = Usual(),
        };

        var prompt = MemberChatService.FormatFetchedData(
            data, Today, ageYears: 82, askedMetrics: [ChartMetricKind.Sleep]);

        Assert.Contains("\"nights_with_reading\": \"0 of 7\"", prompt, StringComparison.Ordinal);
        Assert.Contains("\"average\": null", prompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// Breathing asleep is graded against the member alone: WHO's 12–20 is a waking rate at rest,
    /// and <see cref="HealthReferenceRanges.NoOvernightBreathingBand"/> forbids printing it beside an
    /// overnight figure.
    /// </summary>
    [Fact]
    public void BreathingAsleep_CarriesNoPublishedRange()
    {
        var rows = ReportedWeek().Select(l => new ActivityLog { Date = l.Date, OvernightBreathingRate = 14.2m }).ToList();

        var breathing = ReadingWindowSummaries.ForMetric(
            ChartMetricKind.OvernightBreathingRate, rows, Week, Today, Usual(), ageYears: 82);

        Assert.NotNull(breathing);
        Assert.Null(breathing.Band);
    }

    /// <summary>
    /// The code-written answer when too few nights reached us: the count against the window, then
    /// the nights that did, newest first — so "how did he sleep last night?", sized at a week by
    /// the planner, still reads its answer first.
    /// </summary>
    [Fact]
    public void TheTooFewReply_LeadsWithLastNight_AndNamesTheCount()
    {
        var rows = ReportedWeek().Where(l => l.Date >= Today.AddDays(-2)).ToList();
        var sleep = ReadingWindowSummaries.ForMetric(
            ChartMetricKind.Sleep, rows, Week, Today, Usual(), ageYears: 82)!;

        var reply = MemberChatReplies.TooFewReadingsReply([sleep], Week.From, Today);

        Assert.Equal(
            "Only 3 of the 7 nights from Sep 19 to Sep 25 have a sleep reading that reached us — too few "
            + "to give an average for that stretch. The nights that did: last night 3h 45m, the night before "
            + "4h 20m, Sep 23 5h 40m.",
            reply);
    }

    [Fact]
    public void TheTooFewReply_SaysPlainlyWhenNothingArrived()
    {
        var sleep = ReadingWindowSummaries.ForMetric(
            ChartMetricKind.Sleep, [], Week, Today, Usual(), ageYears: 82)!;

        var reply = MemberChatReplies.TooFewReadingsReply([sleep], Week.From, Today);

        Assert.Equal(
            "No sleep reading has reached us for the 7 nights from Sep 19 to Sep 25, so there is nothing "
            + "to go on for that stretch.",
            reply);
    }
}
