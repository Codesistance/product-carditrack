using CardiTrack.Mobile.Core.Localization;

namespace CardiTrack.UnitTests.Mobile;

public class PhonePlaceholderTests
{
    [Theory]
    [InlineData("GB", "+44 7700 900000")]
    [InlineData("gb", "+44 7700 900000")]
    [InlineData("US", "+1 555 000 0000")]
    [InlineData("CA", "+1 555 000 0000")]
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
        Assert.Equal("+1 555 000 0000", PhonePlaceholder.ForRegion(region));
    }
}
