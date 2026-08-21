using CardiTrack.Infrastructure.Services;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// The placeholder is what lets generated copy say "Dad" while the model is never told who Dad is.
/// It works in both directions, and each direction fails differently: outbound
/// (<see cref="NamePlaceholder.Redact"/>) a miss sends a real name to a third-party provider, and
/// inbound (<see cref="NamePlaceholder.Resolve"/>) a miss shows a caregiver a raw sentinel where
/// their father's name should be. Inbound runs on text a 4B model wrote, so its matching has to
/// survive the shapes such a model actually returns.
/// </summary>
public class NamePlaceholderTests
{
    [Theory]
    [InlineData("CardiTrackCardiMember slept well.", "Dad slept well.")]
    [InlineData("carditrackcardimember slept well.", "Dad slept well.")]
    [InlineData("CardiTrack CardiMember slept well.", "Dad slept well.")]
    [InlineData("CardiTrack_Cardi_Member slept well.", "Dad slept well.")]
    [InlineData("CardiTrack-Cardi-Member slept well.", "Dad slept well.")]
    [InlineData("CardiTrackCardiMember's steps are down.", "Dad's steps are down.")]
    [InlineData("CardiTrackCardiMember rested, and CardiTrackCardiMember walked.", "Dad rested, and Dad walked.")]
    public void Resolve_HandlesTheShapesASmallModelReturns(string generated, string expected) =>
        Assert.Equal(expected, NamePlaceholder.Resolve(generated, "Dad"));

    [Fact]
    public void Resolve_LeavesTextAlone_WhenThereIsNoPlaceholder() =>
        Assert.Equal("A quiet day.", NamePlaceholder.Resolve("A quiet day.", "Dad"));

    /// <summary>
    /// Returning the text unchanged rather than deleting the token is deliberate: the caller has
    /// to be able to tell that it could not be resolved, so it can discard the generation instead
    /// of storing a sentence with a hole where a name should be.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_LeavesThePlaceholderStanding_WhenThereIsNoName(string? name) =>
        Assert.Equal("CardiTrackCardiMember slept well.", NamePlaceholder.Resolve("CardiTrackCardiMember slept well.", name));

    [Theory]
    [InlineData("CardiTrackCardiMember slept well.", true)]
    [InlineData("cardiTrack cardi member slept well.", true)]
    [InlineData("Dad slept well.", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsPresentIn_DetectsWhatResolveWouldReplace(string? text, bool expected) =>
        Assert.Equal(expected, NamePlaceholder.IsPresentIn(text));

    [Theory]
    [InlineData("Dad", "Dad")]
    [InlineData("Dad Smith", "Dad")]
    [InlineData("  Mary  Jane  ", "Mary")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void FirstName_TakesWhatAFamilyMemberWouldSayAloud(string? full, string? expected) =>
        Assert.Equal(expected, NamePlaceholder.FirstName(full));

    /// <summary>
    /// The outbound direction, and the one that carries compliance weight: text that has already
    /// been through <see cref="NamePlaceholder.Resolve"/> — a stored chat reply, say — must go
    /// back out carrying the token, not the name.
    /// </summary>
    [Theory]
    [InlineData("Margaret walked more today.")]
    [InlineData("margaret walked more today.")]
    [InlineData("MARGARET walked more today.")]
    [InlineData("Margaret's steps are up, and Margaret slept well.")]
    public void Redact_SwapsEveryCasingOfTheName(string stored)
    {
        var redacted = NamePlaceholder.Redact(stored, "Margaret Doe");

        Assert.DoesNotContain("argaret", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(NamePlaceholder.Token, redacted);
    }

    /// <summary>
    /// Longest form first, so the full name is taken whole. Matching the first name first would
    /// leave the surname stranded beside the token — "CardiTrackCardiMember Doe" — which is the
    /// leak this method exists to stop, only quieter.
    /// </summary>
    [Fact]
    public void Redact_TakesTheFullNameWhole_LeavingNoSurnameBehind() =>
        Assert.Equal(
            $"{NamePlaceholder.Token} walked more today.",
            NamePlaceholder.Redact("Margaret Doe walked more today.", "Margaret Doe"));

    /// <summary>
    /// Word-bounded: a name that happens to sit inside a longer word is not the member being
    /// named, and replacing it would corrupt the sentence the model has to read.
    /// </summary>
    [Fact]
    public void Redact_DoesNotMatchInsideALongerWord() =>
        Assert.Equal("Marginal changes only.", NamePlaceholder.Redact("Marginal changes only.", "Mar"));

    /// <summary>
    /// A single-letter first name is skipped on purpose — the word boundary would fire on every
    /// stray initial in the text, which mangles the context without protecting anything, since a
    /// lone letter identifies nobody.
    /// </summary>
    [Fact]
    public void Redact_SkipsASingleLetterFirstName() =>
        Assert.Equal(
            $"{NamePlaceholder.Token} rested. X marks the chart.",
            NamePlaceholder.Redact("X Doe rested. X marks the chart.", "X Doe"));

    [Theory]
    [InlineData(null, "Margaret")]
    [InlineData("", "Margaret")]
    [InlineData("Margaret walked.", null)]
    [InlineData("Margaret walked.", "   ")]
    public void Redact_LeavesTextAlone_WhenEitherSideIsMissing(string? text, string? name) =>
        Assert.Equal(text, NamePlaceholder.Redact(text, name));

    /// <summary>
    /// The round trip a chat turn actually makes: generated with the token, resolved for storage
    /// and display, then redacted again on its way back into the next prompt as history. What the
    /// model sees on the second pass must be what it saw on the first.
    /// </summary>
    [Fact]
    public void RedactUndoesResolve_SoRecalledHistoryCarriesTheTokenAgain()
    {
        const string generated = "CardiTrackCardiMember walked 4,200 steps.";

        var stored = NamePlaceholder.Resolve(generated, "Margaret");
        Assert.Equal("Margaret walked 4,200 steps.", stored);

        Assert.Equal(generated, NamePlaceholder.Redact(stored, "Margaret"));
    }
}
