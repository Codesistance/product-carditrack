using CardiTrack.Application.DTOs.Common;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Services;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// The comparisons every chat chart now travels with, so the client can plot them beside the
/// values the way the CardiMember Details trends do. The rules pinned here are the same ones the
/// clinical prompt's bands block states in words: a published band only where one is published
/// (never steps, never overnight HRV), the member's own baseline wherever it is learned, and
/// every comparison in the series' own unit — sleep's band arrives in minutes because its points
/// do, whatever unit the publisher writes in.
/// </summary>
public class MemberChatChartComparisonTests
{
    private static FetchedMemberData Data(PatternBaseline? baseline = null) => new()
    {
        RecentActivity =
        [
            new ActivityLog
            {
                Date = new DateOnly(2026, 8, 23),
                Steps = 4200,
                RestingHeartRate = 62,
                SleepMinutes = 400,
                HeartRateVariabilityMs = 41m,
                OvernightBreathingRate = 14.2m,
            },
        ],
        Baseline = baseline,
    };

    private static PatternBaseline Baseline() => new()
    {
        AvgSteps = 5100,
        AvgRestingHeartRate = 64,
        AvgSleepMinutes = 430,
        AvgHeartRateVariabilityMs = 38m,
        AvgOvernightBreathingRate = 13.5m,
    };

    private static ChartSeries Series(IReadOnlyList<ChartSeries> charts, string metric) =>
        Assert.Single(charts, c => c.Metric == metric);

    [Fact]
    public void EverySeries_CarriesTheMembersOwnBaseline()
    {
        var charts = MemberChatService.BuildCharts(Data(Baseline()), metrics: null, ageYears: 70);

        Assert.Equal(5100, Series(charts, "Steps").Baseline);
        Assert.Equal(64, Series(charts, "Resting heart rate").Baseline);
        Assert.Equal(430, Series(charts, "Sleep").Baseline);
        Assert.Equal(38, Series(charts, "Heart rate variability").Baseline);
        Assert.Equal(13.5, Series(charts, "Breathing while asleep").Baseline);
    }

    /// <summary>
    /// The bands are the published ones <see cref="HealthReferenceRanges"/> attributes — and only
    /// those. Steps and overnight HRV carry none on purpose: no accredited body publishes one,
    /// and inventing "10,000 steps" for the chart would be the exact overreach the registry
    /// forbids the prompts.
    /// </summary>
    [Fact]
    public void PublishedBands_AttachOnlyWhereOneIsPublished()
    {
        var charts = MemberChatService.BuildCharts(Data(), metrics: null, ageYears: 70);

        var heart = Series(charts, "Resting heart rate").Reference;
        Assert.NotNull(heart);
        Assert.Equal((60, 100, "AHA"), (heart.Low, heart.High, heart.Source));

        // No band behind breathing asleep: WHO's 12–20 is a waking rate at rest
        // (HealthReferenceRanges.NoOvernightBreathingBand).
        Assert.Null(Series(charts, "Breathing while asleep").Reference);

        Assert.Null(Series(charts, "Steps").Reference);
        Assert.Null(Series(charts, "Heart rate variability").Reference);
    }

    /// <summary>The band travels in the unit the points are in: NSF publishes hours, the sleep
    /// series is minutes, so 7–9h becomes 420–540 — and the 65+ ceiling of 8h becomes 480.</summary>
    [Theory]
    [InlineData(50, 420, 540)]
    [InlineData(70, 420, 480)]
    public void TheSleepBand_ArrivesInMinutes_AndTakesTheAgeSplit(int age, int low, int high)
    {
        var charts = MemberChatService.BuildCharts(Data(), metrics: null, ageYears: age);

        var sleep = Series(charts, "Sleep").Reference;
        Assert.NotNull(sleep);
        Assert.Equal((low, high, HealthReferenceRanges.SleepSource), (sleep.Low, sleep.High, sleep.Source));
        // Converting to minutes must not drop what the band is: the normal, not background.
        Assert.True(sleep.IsPublishedNormal);
    }

    /// <summary>No member row means no age, and the sleep chart then draws no band rather than a
    /// guessed one — the stance every other composer on the platform already takes.</summary>
    [Fact]
    public void NoAge_MeansNoSleepBand_NotAGuessedOne()
    {
        var charts = MemberChatService.BuildCharts(Data(), metrics: null, ageYears: null);

        Assert.Null(Series(charts, "Sleep").Reference);
    }

    /// <summary>A member whose baseline is still learning charts without the rule, not without
    /// the chart.</summary>
    [Fact]
    public void NoBaselineYet_ChartsWithoutTheRule()
    {
        var charts = MemberChatService.BuildCharts(Data(baseline: null), metrics: null, ageYears: 70);

        Assert.Equal(5, charts.Count);
        Assert.All(charts, c => Assert.Null(c.Baseline));
    }

    /// <summary>An Awake night is stored as 0 minutes; the point carries the status so the chat
    /// chart can say "awake all night" rather than "0m". Only the sleep series carries one.</summary>
    [Fact]
    public void SleepPoints_CarryTheNightStatus_SoAnAwakeZeroIsNotReadAsAReading()
    {
        var data = new FetchedMemberData
        {
            RecentActivity =
            [
                new ActivityLog { Date = new DateOnly(2026, 8, 22), SleepMinutes = 0, RestingHeartRate = 66, NightStatus = NightSleepStatus.Awake },
                new ActivityLog { Date = new DateOnly(2026, 8, 23), SleepMinutes = 400, RestingHeartRate = 62, NightStatus = NightSleepStatus.Slept },
            ],
        };

        var charts = MemberChatService.BuildCharts(data, metrics: null, ageYears: 70);

        var sleep = Series(charts, "Sleep").Points;
        Assert.Equal(NightSleepStatus.Awake, sleep.Single(p => p.Value == 0).Night);
        Assert.Equal(NightSleepStatus.Slept, sleep.Single(p => p.Value == 400).Night);
        Assert.All(Series(charts, "Resting heart rate").Points, p => Assert.Null(p.Night));
    }

    /// <summary>Chat turns persist their charts as JSON. A turn stored before points carried a
    /// night status must still load — to a point without one, not to an error.</summary>
    [Fact]
    public void AChartStoredBeforeNightStatus_StillDeserialises()
    {
        const string stored = """[{"Metric":"Sleep","Points":[{"Date":"2026-08-23","Value":400}],"Baseline":430,"Reference":null}]""";

        var charts = System.Text.Json.JsonSerializer.Deserialize<List<ChartSeries>>(stored)!;

        var point = Assert.Single(Assert.Single(charts).Points);
        Assert.Equal(400, point.Value);
        Assert.Null(point.Night);
    }
}
