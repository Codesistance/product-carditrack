using CardiTrack.API.Validators;
using CardiTrack.Application.DTOs.Requests;

namespace CardiTrack.IntegrationTests.Validators;

/// <summary>
/// What the standing-fact endpoint accepts. Same cap as an answer — this is the same store —
/// and blank is not a fact: there is no skip action on this path, so an empty body is refused.
/// </summary>
public class OfferStandingFactValidatorTests
{
    private readonly OfferStandingFactValidator _sut = new();

    private bool IsValid(string? fact) =>
        _sut.Validate(new OfferStandingFactRequest { FactText = fact! }).IsValid;

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void Rejects_AFactWithNothingInIt(string? fact)
    {
        Assert.False(IsValid(fact));
    }

    [Fact]
    public void Accepts_AnOrdinaryFact()
    {
        Assert.True(IsValid("She moved to the downstairs bedroom last week."));
    }

    [Fact]
    public void Rejects_AFactPastTheStorageCap()
    {
        Assert.True(IsValid(new string('a', 2_000)));
        Assert.False(IsValid(new string('a', 2_001)));
    }
}
