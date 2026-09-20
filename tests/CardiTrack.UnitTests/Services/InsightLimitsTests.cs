using CardiTrack.Application.Services;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// The budgets the insight writers fit their text to. The failure these prevent is not cosmetic:
/// a reply longer than its column fails the save, which costs the member the insight and leaves
/// the row a backfill candidate that fails again at the same length on every later pass.
/// </summary>
public class InsightLimitsTests
{
    [Fact]
    public void TextInsideTheBudgetIsUntouched()
    {
        // The case every real reply takes. Nothing about a valid answer should be rewritten.
        const string summary = "She has been steadier this week than last, and sleeping longer.";

        Assert.Equal(summary, InsightLimits.Fit(summary, InsightLimits.Summary));
        Assert.Null(InsightLimits.Fit(null, InsightLimits.Summary));
    }

    [Fact]
    public void AnOverlongReplyIsCutBackToItsLastWholeSentence()
    {
        var text = string.Join(" ", Enumerable.Repeat("She slept well last night.", 200));

        var fitted = InsightLimits.Fit(text, InsightLimits.Summary)!;

        Assert.True(fitted.Length <= InsightLimits.Summary);
        Assert.EndsWith("night.", fitted);
        Assert.DoesNotContain("…", fitted);
    }

    [Fact]
    public void ARunOnWithNoSentenceEndFallsBackToAHardCut()
    {
        // A model that answered in one enormous clause. Nothing to trim back to, so the cut reads
        // as deliberate rather than as the product breaking off.
        var text = new string('a', InsightLimits.Summary * 2);

        var fitted = InsightLimits.Fit(text, InsightLimits.Summary)!;

        Assert.Equal(InsightLimits.Summary, fitted.Length);
        Assert.EndsWith("…", fitted);
    }

    [Fact]
    public void FindingsAreDroppedWholeRatherThanCutInHalf()
    {
        // A findings block is a list of separate claims, and half a claim is not a shorter claim.
        var findings = new[]
        {
            "Her resting heart rate is a little above her usual.",
            new string('b', InsightLimits.KeyFindings),
            "She has been walking more on weekdays.",
        };

        var joined = InsightLimits.JoinFindings(findings)!;

        Assert.True(joined.Length <= InsightLimits.KeyFindings);
        Assert.Contains("resting heart rate", joined);
        Assert.Contains("walking more", joined);
        Assert.DoesNotContain("bbb", joined);
    }

    [Fact]
    public void NothingLeftIsNullRatherThanAnEmptyString()
    {
        Assert.Null(InsightLimits.JoinFindings([]));
        Assert.Null(InsightLimits.JoinFindings([new string('c', InsightLimits.KeyFindings + 1)]));
    }
}
