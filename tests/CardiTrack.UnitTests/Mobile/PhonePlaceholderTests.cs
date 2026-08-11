using CardiTrack.Mobile.Core.Localization;

namespace CardiTrack.UnitTests.Mobile;

public class PhonePlaceholderTests
{
    [Theory]
    [InlineData("GB", "+447700900000")]
    [InlineData("gb", "+447700900000")]
    [InlineData("US", "+15550000000")]
    [InlineData("CA", "+15550000000")]
    public void ForRegion_KnownRegion_ReturnsRegionalExample(string region, string expected)
    {
        Assert.Equal(expected, PhonePlaceholder.ForRegion(region));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("FR")]
    [InlineData("ZZ")]
    public void ForRegion_UnknownOrMissingRegion_FallsBackToUsExample(string? region)
    {
        Assert.Equal("+15550000000", PhonePlaceholder.ForRegion(region));
    }

    // Every example must itself pass the E.164-style validator the mobile forms and the API
    // apply (^\+?[1-9]\d{1,14}$) — a placeholder a caregiver could type verbatim and have
    // rejected would be worse than no example at all.
    [Theory]
    [InlineData("GB")]
    [InlineData("US")]
    [InlineData("CA")]
    [InlineData(null)]
    public void ForRegion_EveryExample_MatchesTheE164Validator(string? region)
    {
        Assert.Matches(@"^\+?[1-9]\d{1,14}$", PhonePlaceholder.ForRegion(region));
    }
}
