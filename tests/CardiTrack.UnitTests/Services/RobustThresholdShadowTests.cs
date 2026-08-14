using CardiTrack.Application.Services;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// Pins the G2 shadow comparison: the live 30%-of-mean fence versus a 3× unscaled-MAD fence.
/// Neither path here writes an alert.
/// </summary>
public class RobustThresholdShadowTests
{
    [Fact]
    public void CompareDecline_BothFire_OnALargeDropWithModestMad()
    {
        // Usual 5_000, today 2_000: 60% below the mean, and 3_000 below a 5_000 median
        // with MAD 200 (fence is 4_400).
        var verdict = RobustThresholdShadow.CompareDecline(2_000, mean: 5_000, median: 5_000, mad: 200);

        Assert.True(verdict.MeanRuleFires);
        Assert.True(verdict.MadFenceFires);
        Assert.True(verdict.Agree);
    }

    [Fact]
    public void CompareDecline_MeanMissesADropTheMadFenceCatches()
    {
        // 25% below a 5_000 mean is inside the live 30% rule. MAD 100 puts the 3× fence at
        // 4_700, so 3_750 is a miss the robust fence would have caught — G2's false-negative
        // of the mean rule.
        var verdict = RobustThresholdShadow.CompareDecline(3_750, mean: 5_000, median: 5_000, mad: 100);

        Assert.False(verdict.MeanRuleFires);
        Assert.True(verdict.MadFenceFires);
        Assert.True(verdict.MadOnly);
    }

    [Fact]
    public void CompareDecline_MeanFiresOnAVariableMemberTheMadFenceWouldSpare()
    {
        // Outlier-inflated mean 6_000; today 4_100 is more than 30% below that mean. The
        // cluster's median is 4_500 with MAD 800, so 4_100 sits inside the 3× fence (2_100).
        var verdict = RobustThresholdShadow.CompareDecline(4_100, mean: 6_000, median: 4_500, mad: 800);

        Assert.True(verdict.MeanRuleFires);
        Assert.False(verdict.MadFenceFires);
        Assert.True(verdict.MeanOnly);
    }

    [Fact]
    public void CompareDecline_DoesNotInventAFence_WhenMadIsZero()
    {
        var verdict = RobustThresholdShadow.CompareDecline(1, mean: 5_000, median: 5_000, mad: 0);

        Assert.True(verdict.MeanRuleFires);
        Assert.False(verdict.MadFenceFires);
    }

    [Fact]
    public void CompareTwoSided_FiresOnALongNightTheMeanRuleMisses()
    {
        // Usual 420 minutes; tonight 520 is ~24% above the mean (inside 30%) but 100 above
        // a 420 median with MAD 10 (fence ±30).
        var verdict = RobustThresholdShadow.CompareTwoSided(520, mean: 420, median: 420, mad: 10);

        Assert.False(verdict.MeanRuleFires);
        Assert.True(verdict.MadFenceFires);
        Assert.True(verdict.MadOnly);
    }

    [Fact]
    public void CompareTwoSided_DoesNotFire_InsideBothFences()
    {
        var verdict = RobustThresholdShadow.CompareTwoSided(430, mean: 420, median: 420, mad: 20);

        Assert.False(verdict.MeanRuleFires);
        Assert.False(verdict.MadFenceFires);
        Assert.True(verdict.Agree);
    }
}
