using CardiTrack.Application.Services;

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
}
