using CardiTrack.Infrastructure.Services;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// The placeholder is what lets generated copy say "Dad" while the model is never told who Dad is.
/// Its whole job is done on the way out, on text a 4B model wrote, so the matching has to survive
/// the shapes such a model actually returns — the failure it guards against is literal braces
/// reaching a caregiver's summary.
/// </summary>
public class NamePlaceholderTests
{
    [Theory]
    [InlineData("{{NAME}} slept well.", "Dad slept well.")]
    [InlineData("{{name}} slept well.", "Dad slept well.")]
    [InlineData("{{ NAME }} slept well.", "Dad slept well.")]
    [InlineData("{NAME} slept well.", "Dad slept well.")]
    [InlineData("{{NAME}}'s steps are down.", "Dad's steps are down.")]
    [InlineData("{{NAME}} rested, and {{NAME}} walked.", "Dad rested, and Dad walked.")]
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
        Assert.Equal("{{NAME}} slept well.", NamePlaceholder.Resolve("{{NAME}} slept well.", name));

    [Theory]
    [InlineData("{{NAME}} slept well.", true)]
    [InlineData("{{ name }} slept well.", true)]
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
}
