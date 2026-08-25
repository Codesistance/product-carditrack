using CardiTrack.Application.Services;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// The URL behind a quoted citation line. Same contract as the citation text itself: the client
/// only ever asks which fixed line it is holding, and the words — here, the address — come from
/// the closed set, so a link a caregiver taps is checkably the source the suggestion was grounded
/// in and never something composed.
/// </summary>
public class CitationLinksTests
{
    /// <summary>Every advise citation resolves to a real, secure address — a served Reference
    /// line's authority is always offered as a way to the source.</summary>
    [Fact]
    public void EveryWellnessCitation_CarriesAnHttpsSource()
    {
        foreach (var reference in WellnessGuidelines.All)
        {
            var url = CitationLinks.UrlFor(reference.Citation);

            Assert.NotNull(url);
            Assert.StartsWith("https://", url, StringComparison.Ordinal);
        }
    }

    /// <summary>The inference bands with a canonical page resolve; the WHO breathing band —
    /// textbook consensus with no single publication behind it — deliberately does not.</summary>
    [Fact]
    public void PublishedBands_ResolveOnlyWhereACanonicalPageExists()
    {
        foreach (var band in ChatDataRegistry.Bands)
            Assert.Equal(band.Url, CitationLinks.UrlFor(band.Citation));

        Assert.Contains(ChatDataRegistry.Bands, b => b.Url is null);
        Assert.Contains(ChatDataRegistry.Bands, b => b.Url is not null);
    }

    /// <summary>Anything the closed sets did not write gets no link — never a guess. Exact text
    /// only, bar the whitespace a client-side parse leaves around a line.</summary>
    [Theory]
    [InlineData("World Health Organization — a citation nobody wrote")]
    [InlineData("")]
    [InlineData("   ")]
    public void AnythingOutsideTheSets_GetsNoLink(string citation) =>
        Assert.Null(CitationLinks.UrlFor(citation));

    /// <summary>The client hands this the citation as it parsed it out of a reply line — which
    /// may carry the surrounding whitespace the split left behind.</summary>
    [Fact]
    public void AParsedCitation_MatchesDespiteSurroundingWhitespace()
    {
        var citation = WellnessGuidelines.All[0].Citation;

        Assert.NotNull(CitationLinks.UrlFor($" {citation} "));
    }
}
