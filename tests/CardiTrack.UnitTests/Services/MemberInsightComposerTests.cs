using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// One mapping for three surfaces. What matters here is what it declines to say: an absent trend
/// must not read as a known one, and a stale row must not reach a screen the API would withhold it
/// from.
/// </summary>
public class MemberInsightComposerTests
{
    private static readonly DateTime Now = new(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Under <see cref="HealthReferenceRanges.OlderAdultAge"/>, so the sleep band these tests
    /// would be graded against is the 7–9 hour one. Nothing here carries a movement, so the age
    /// only has to be a legal one.
    /// </summary>
    private const int Age = 54;

    [Fact]
    public void NeitherRowMeansNoBlockAtAll()
    {
        // Null rather than an empty block, so a client renders the section on presence instead of
        // testing every field for emptiness.
        Assert.Null(MemberInsightComposer.Compose(null, null, Now, Age));
    }

    [Fact]
    public void BothRowsAreCarriedTogether()
    {
        var block = MemberInsightComposer.Compose(
            Baseline("Steps are down on their usual.", "Steps down.\nSleep steady."),
            Trend("Activity has eased off over the month.", "Weeks of fewer steps."),
            Now,
            Age);

        Assert.Equal("Steps are down on their usual.", block!.Summary);
        Assert.Equal(["Steps down.", "Sleep steady."], block.KeyFindings);
        Assert.Equal("Activity has eased off over the month.", block.Trend);
        Assert.Equal(["Weeks of fewer steps."], block.TrendFindings);
        Assert.Equal(30, block.BaselinePeriodDays);
    }

    [Fact]
    public void AMemberWithNoTrendYetIsStillReadAgainstTheirBaseline()
    {
        // Under a month of readings: the trend pass declines, and the baseline half stands alone
        // rather than the whole block going quiet.
        var block = MemberInsightComposer.Compose(Baseline("Early days, but steady.", null), null, Now, Age);

        Assert.NotNull(block);
        Assert.Null(block!.Trend);
        Assert.Empty(block.TrendFindings);
    }

    [Fact]
    public void TheLearningStateComesFromTheBaselineRowAlone()
    {
        // A trend row cannot speak to it — the trend pass never runs for a member still being
        // learned — so reading it here would let an absent trend look like an established baseline.
        var learning = Baseline("Getting to know them.", null);
        learning.IsLearning = true;

        var block = MemberInsightComposer.Compose(learning, null, Now, Age);

        Assert.True(block!.IsLearning);
    }

    [Fact]
    public void AStaleRowIsDroppedRatherThanShown()
    {
        var stale = Baseline("Written a fortnight ago.", null);
        stale.GeneratedAtUtc = Now - InsightServability.MaxAge - TimeSpan.FromDays(1);

        var block = MemberInsightComposer.Compose(stale, Trend("Still current.", null), Now, Age);

        Assert.Null(block!.Summary);
        Assert.Equal("Still current.", block.Trend);
        // With no servable baseline, the block reports the learning state rather than claiming a
        // baseline it is not showing.
        Assert.True(block.IsLearning);
    }

    [Fact]
    public void TheStampIsTheNewerOfTheTwo()
    {
        var block = MemberInsightComposer.Compose(
            WithTime(Baseline("Recent.", null), Now.AddHours(-1)),
            WithTime(Trend("Older.", null), Now.AddHours(-20)),
            Now,
            Age);

        Assert.Equal(Now.AddHours(-1), block!.GeneratedAt);
    }

    [Fact]
    public void TheStoredMovementsComeBackGraded()
    {
        var row = Baseline("Sleeping less than usual.", "Sleep down.");
        row.Movements = InsightMovements.Write(
        [
            new MetricMovement(
                TrackedMetric.Sleep, "Sleep", "hours a night", 5.7m, 7.2m, -21m, MeasuredDays: 7),
        ]);

        var movement = Assert.Single(MemberInsightComposer.Compose(row, null, Now, Age)!.Movements);

        Assert.Equal("Sleep", movement.Metric);
        Assert.Equal("attention", movement.Valence);
        Assert.Contains("NSF", movement.Basis);

        // The figures reach the card as figures, so it never has to parse them back out of the
        // sentence a model wrote — which is the whole reason the column exists.
        Assert.Equal(5.7m, movement.Recent);
        Assert.Equal(7.2m, movement.Usual);
        Assert.Contains("5.7", movement.Headline);
        Assert.Contains("7.2", movement.Headline);

        Assert.Equal("SleepMinutes", movement.AlarmMetric);
        Assert.Equal(21m, movement.SuggestedThresholdPercent);
    }

    [Fact]
    public void AMovementWithNoAlarmIsOfferedNoThreshold()
    {
        // A suggested level for an alarm that cannot be created is a number with nowhere to go.
        var row = Baseline("Moving less than usual.", "Active minutes down.");
        row.Movements = InsightMovements.Write(
        [
            new MetricMovement(
                TrackedMetric.ActiveMinutes, "Active minutes", "minutes a day", 14m, 31m, -55m,
                MeasuredDays: 7),
        ]);

        var movement = Assert.Single(MemberInsightComposer.Compose(row, null, Now, Age)!.Movements);

        Assert.Null(movement.AlarmMetric);
        Assert.Null(movement.SuggestedThresholdPercent);
    }

    [Fact]
    public void AStaleRowTakesItsMovementsWithIt()
    {
        // The movements are part of the reading, not a separate fact about the member. A row the
        // API withholds as out of date must not leave its figures on the screen.
        var stale = WithTime(Baseline("Old news.", "Steps down."), Now.AddDays(-30));
        stale.Movements = InsightMovements.Write(
        [
            new MetricMovement(
                TrackedMetric.Steps, "Steps", "steps a day", 3100m, 5400m, -43m, MeasuredDays: 7),
        ]);

        var block = MemberInsightComposer.Compose(stale, Trend("Still current.", null), Now, Age);

        Assert.Empty(block!.Movements);
    }

    private static MemberInsight Baseline(string summary, string? findings) => new()
    {
        CardiMemberId = Guid.NewGuid(),
        Scope = InsightScope.Baseline,
        Summary = summary,
        KeyFindings = findings,
        BaselinePeriodDays = 30,
        GeneratedAtUtc = Now.AddHours(-2),
    };

    private static MemberInsight Trend(string summary, string? findings) => new()
    {
        CardiMemberId = Guid.NewGuid(),
        Scope = InsightScope.Trend,
        Summary = summary,
        KeyFindings = findings,
        GeneratedAtUtc = Now.AddHours(-6),
    };

    private static MemberInsight WithTime(MemberInsight insight, DateTime generatedAtUtc)
    {
        insight.GeneratedAtUtc = generatedAtUtc;
        return insight;
    }
}
