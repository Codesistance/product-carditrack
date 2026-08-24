using CardiTrack.Application.Services;
using CardiTrack.Infrastructure.Services;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// The closed set of wellness authorities an advise reply may quote. Same contract as the
/// inference citations: the model's stored pick is free text, the quoted words are this class's
/// fixed lines, and the figures in those lines must be the ones the generation prompt actually
/// carried — a citation that drifted from the prompt would quote an authority for a range nobody
/// was grounded in.
/// </summary>
public class WellnessGuidelinesTests
{
    /// <summary>The drift guard: every citation's figures appear in the prompt block the
    /// generation was grounded against (<c>MedicalPromptBlocks.WellnessGuidelineReference</c>).</summary>
    [Theory]
    [InlineData("World Health Organization", "150-300 minutes")]
    [InlineData("AASM/CDC consensus", "7 or more hours")]
    [InlineData("American Heart Association", "60-100 bpm")]
    public void EachCitation_QuotesTheFiguresThePromptCarried(string authority, string figures)
    {
        var reference = Assert.Single(WellnessGuidelines.All, r => r.Authority == authority);

        Assert.Contains(figures, reference.Citation, StringComparison.Ordinal);
        Assert.Contains(figures, MedicalPromptBlocks.WellnessGuidelineReference, StringComparison.Ordinal);
    }

    /// <summary>The mapping recognises the phrasings the prompt teaches the model — and the
    /// free-text picks rows already stored carry.</summary>
    [Theory]
    [InlineData("Adult physical activity (WHO, 2020)", "World Health Organization")]
    [InlineData("Adult sleep duration (AASM/CDC)", "AASM/CDC consensus")]
    [InlineData("aasm/cdc sleep consensus", "AASM/CDC consensus")]
    [InlineData("Resting heart rate (AHA general reference)", "American Heart Association")]
    public void StoredPicks_MapToTheirAuthority(string stored, string authority)
    {
        var citation = WellnessGuidelines.CitationFor(stored);

        Assert.NotNull(citation);
        Assert.StartsWith(authority, citation, StringComparison.Ordinal);
    }

    /// <summary>Nothing in the set, nothing quoted — blank, placeholder-ish, or an authority the
    /// model composed. The wearable caveat bullet is deliberately uncitable: it is a limitation
    /// note, not an authority a suggestion can rest on.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("General wellbeing literature")]
    [InlineData("SpO2 wearable caveat")]
    public void AnythingOutsideTheSet_QuotesNothing(string? stored) =>
        Assert.Null(WellnessGuidelines.CitationFor(stored));
}
