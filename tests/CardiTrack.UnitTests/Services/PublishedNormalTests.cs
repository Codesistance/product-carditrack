using CardiTrack.Application.Services;
using CardiTrack.Infrastructure.Services;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// The one statement of what "normal" means for a reading (decision 2026-09-25): the published
/// range for sleep, resting heart rate and blood oxygen, the member's own usual for the readings
/// with none.
/// </summary>
public class PublishedNormalTests
{
    [Fact]
    public void TheRanges_AreTheMembersOwn_SleepResolvedToTheirAge()
    {
        var ranges = PublishedNormal.Ranges(80);

        Assert.Contains("- Sleep: 7-8 hours a night (420-480 minutes), recommended at this member's age (NSF).", ranges);
        Assert.Contains("- Resting heart rate: 60-100 bpm for an adult at rest (AHA).", ranges);
        Assert.Contains("- Blood oxygen: 94-100% (WHO).", ranges);
    }

    [Fact]
    public void WithNoAge_BothSleepBandsAreGiven_RatherThanOneGuessed() =>
        Assert.Contains("7-9 hours a night for adults, 7-8 from 65", PublishedNormal.Ranges(null));

    /// <summary>
    /// Breathing asleep, HRV and steps are named as having no range — left out, the model supplies
    /// one from memory, and WHO's 12–20 is the waking rate it would reach for.
    /// </summary>
    [Fact]
    public void TheReadingsWithNoRange_AreNamedAsHavingNone()
    {
        var ranges = PublishedNormal.Ranges(80);

        Assert.Contains("- Breathing while asleep: Breathing asleep is compared against the member's own baseline", ranges);
        Assert.Contains("- Heart rate variability:", ranges);
        Assert.Contains("- Steps: no published range", ranges);
        Assert.DoesNotContain("12-20", ranges);
    }

    /// <summary>The rule says the range decides and the usual is context — never the reverse, which is what the prompts said before.</summary>
    [Fact]
    public void TheRule_PutsTheRangeFirst()
    {
        Assert.Contains("the published range listed with the readings is what normal means", PublishedNormal.Rule);
        Assert.Contains("worth attention even when it is usual for this person", PublishedNormal.Rule);
        Assert.Contains("never whether it is healthy", PublishedNormal.Rule);
        Assert.StartsWith(PublishedNormal.Ranges(70), PublishedNormal.Block(70));
    }

    /// <summary>The stance chat used to state, gone from the block every chat read carries.</summary>
    [Fact]
    public void ChatsBandsBlock_NoLongerLetsTheUsualOverruleTheRange()
    {
        Assert.DoesNotContain("not by itself abnormal", ChatDataRegistry.BandsBlock);
        Assert.Contains("the published range is what normal means", ChatDataRegistry.BandsBlock);
    }

    /// <summary>Every clinical brief opens on the published ranges as well as the baselines.</summary>
    [Fact]
    public void TheClinicalOpening_NamesThePublishedRanges() =>
        Assert.Contains(
            "against the published normal ranges given for sleep, resting heart rate and blood oxygen",
            MedicalPromptBlocks.WearableClinicalRole);
}
