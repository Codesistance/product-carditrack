using CardiTrack.Application.DTOs.Common;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// The authorities an inference reply quotes. The model only ever picks WHICH published range its
/// verdict drew on; the citation text is the registry's fixed lines — so what these tests hold is
/// the closed vocabulary: real authorities in, invented ones dropped, and the quoted figures the
/// same ones the prompt's bands block actually carried.
/// </summary>
public class InferenceCitationTests
{
    [Fact]
    public void EachBand_NamesItsAuthority_AndItsCitationQuotesTheSameFigures()
    {
        foreach (var band in ChatDataRegistry.Bands)
        {
            Assert.False(string.IsNullOrWhiteSpace(band.Authority));
            Assert.StartsWith(band.Authority, band.Citation, StringComparison.Ordinal);
        }

        // The figures a caregiver reads as an authority must be the ones the verdict was judged
        // against — the same numbers the band line put in front of the model.
        Assert.Contains("60–100 bpm", ChatDataRegistry.CitationsFor(["American Heart Association"]).Single());
        Assert.Contains("7–9 hours", ChatDataRegistry.CitationsFor(["National Sleep Foundation"]).Single());
        Assert.Contains("12–20 breaths", ChatDataRegistry.CitationsFor(["World Health Organization"]).Single());
    }

    /// <summary>The band lines attribute "(WHO)" while the authority is spelled out, and the model
    /// may echo either — both spellings reach the same citation.</summary>
    [Theory]
    [InlineData("WHO")]
    [InlineData("World Health Organization")]
    [InlineData("world health organization")]
    public void InitialsAndFullNames_BothMatch(string named) =>
        Assert.Single(ChatDataRegistry.CitationsFor([named]));

    /// <summary>
    /// An authority the registry does not carry is dropped, never quoted — a model that invents
    /// "Journal of Sleep Studies" gets silence, not a citation a caregiver would trust.
    /// </summary>
    [Fact]
    public void AnInventedAuthority_IsDropped_NotQuoted()
    {
        var citations = ChatDataRegistry.CitationsFor(
            ["Journal of Sleep Studies", "American Heart Association", ""]);

        var citation = Assert.Single(citations);
        Assert.StartsWith("American Heart Association", citation, StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicatesCollapse_AndNothingNamed_QuotesNothing()
    {
        Assert.Single(ChatDataRegistry.CitationsFor(["AHA", "American Heart Association"]));
        Assert.Empty(ChatDataRegistry.CitationsFor([]));
    }

    /// <summary>Registry order, whatever order the model answered in — the reply's references
    /// read in the same order the bands block presents them.</summary>
    [Fact]
    public void CitationsComeBack_InRegistryOrder()
    {
        var citations = ChatDataRegistry.CitationsFor(
            ["World Health Organization", "American Heart Association"]);

        Assert.Equal(2, citations.Count);
        Assert.StartsWith("American Heart Association", citations[0], StringComparison.Ordinal);
        Assert.StartsWith("World Health Organization", citations[1], StringComparison.Ordinal);
    }

    // ---- what the verdict actually used ---------------------------------------------------

    private static readonly string[] AllThree =
        ["American Heart Association", "National Sleep Foundation", "World Health Organization"];

    private static FetchedMemberData Fetched(int? hr = null, int? sleep = null, int? breathing = null) => new()
    {
        RecentActivity =
        [
            new ActivityLog
            {
                Date = new DateOnly(2026, 9, 7),
                RestingHeartRate = hr,
                SleepMinutes = sleep,
                OvernightBreathingRate = breathing,
            },
        ],
        RecentActivityWindow = (new DateOnly(2026, 9, 7), new DateOnly(2026, 9, 7)),
    };

    /// <summary>
    /// The footer that appeared under every reply: the model, shown all three bands, named all
    /// three. A verdict that mentions only heart rate has used only the heart rate authority.
    /// </summary>
    [Fact]
    public void AnAuthorityForAMetricTheVerdictNeverMentions_IsDropped()
    {
        var citations = ChatDataRegistry.CitationsFor(
            AllThree, "Settled. Resting HR 62 bpm sits at his usual.", Fetched(hr: 62, sleep: 420, breathing: 14));

        var citation = Assert.Single(citations);
        Assert.StartsWith("American Heart Association", citation, StringComparison.Ordinal);
    }

    /// <summary>
    /// A band the readings never carried was not compared against, whatever the verdict says —
    /// a sleep authority under a verdict written with no sleep figure in front of it.
    /// </summary>
    [Fact]
    public void AnAuthorityForAMetricThatWasNeverFetched_IsDropped()
    {
        var citations = ChatDataRegistry.CitationsFor(
            AllThree, "Sleep at 7 hours and heart rate at 62 both sit inside range.", Fetched(hr: 62));

        var citation = Assert.Single(citations);
        Assert.StartsWith("American Heart Association", citation, StringComparison.Ordinal);
    }

    /// <summary>Named, fetched and mentioned, each in its own spelling — all three survive, in
    /// registry order.</summary>
    [Fact]
    public void AuthoritiesTheVerdictUsed_AllSurvive()
    {
        var citations = ChatDataRegistry.CitationsFor(
            AllThree,
            "Resting HR 62 bpm at baseline; slept 7 h; breathing rate 14/min overnight — all inside range.",
            Fetched(hr: 62, sleep: 420, breathing: 14));

        Assert.Equal(3, citations.Count);
        Assert.StartsWith("American Heart Association", citations[0], StringComparison.Ordinal);
        Assert.StartsWith("National Sleep Foundation", citations[1], StringComparison.Ordinal);
        Assert.StartsWith("World Health Organization", citations[2], StringComparison.Ordinal);
    }

    /// <summary>No readings fetched at all — a verdict from context and baseline alone — quotes
    /// nothing, however many bands the model named.</summary>
    [Fact]
    public void NothingFetched_QuotesNothing()
    {
        var empty = new FetchedMemberData();

        Assert.Empty(ChatDataRegistry.CitationsFor(AllThree, "Heart rate, sleep and breathing all fine.", empty));
    }
}
