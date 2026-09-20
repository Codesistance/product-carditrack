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

    [Fact]
    public void NeitherRowMeansNoBlockAtAll()
    {
        // Null rather than an empty block, so a client renders the section on presence instead of
        // testing every field for emptiness.
        Assert.Null(MemberInsightComposer.Compose(null, null, Now));
    }

    [Fact]
    public void BothRowsAreCarriedTogether()
    {
        var block = MemberInsightComposer.Compose(
            Baseline("Steps are down on their usual.", "Steps down.\nSleep steady."),
            Trend("Activity has eased off over the month.", "Weeks of fewer steps."),
            Now);

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
        var block = MemberInsightComposer.Compose(Baseline("Early days, but steady.", null), null, Now);

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

        var block = MemberInsightComposer.Compose(learning, null, Now);

        Assert.True(block!.IsLearning);
    }

    [Fact]
    public void AStaleRowIsDroppedRatherThanShown()
    {
        var stale = Baseline("Written a fortnight ago.", null);
        stale.GeneratedAtUtc = Now - InsightServability.MaxAge - TimeSpan.FromDays(1);

        var block = MemberInsightComposer.Compose(stale, Trend("Still current.", null), Now);

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
            Now);

        Assert.Equal(Now.AddHours(-1), block!.GeneratedAt);
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
