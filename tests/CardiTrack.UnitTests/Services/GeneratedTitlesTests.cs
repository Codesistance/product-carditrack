using CardiTrack.Infrastructure.Services;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// <see cref="GeneratedTitles"/> — the six-word budget every generated title is held to, and
/// the counting rule behind it.
/// </summary>
public class GeneratedTitlesTests
{
    [Theory]
    [InlineData("All steady", 2)]
    [InlineData("Heart rate a little higher", 5)]
    [InlineData("A quieter-than-usual afternoon", 3)]
    [InlineData("Steps down — heart rate up", 5)]
    [InlineData("  Sleep   quality   this week  ", 4)]
    [InlineData("", 0)]
    [InlineData(null, 0)]
    public void CountsWordsTheWayAReaderWould(string? title, int expected)
    {
        Assert.Equal(expected, GeneratedTitles.WordCount(title));
    }

    [Theory]
    [InlineData("Quiet and steady day so far", false)]
    [InlineData("A quiet and steady day so far", true)]
    public void SixWordsIsTheCap(string title, bool exceeds)
    {
        Assert.Equal(exceeds, GeneratedTitles.ExceedsWordCap(title));
    }

    [Fact]
    public void TruncatesToTheFirstSixWords_AndDropsADanglingDash()
    {
        Assert.Equal(
            "Sleep and heart rate this week",
            GeneratedTitles.TruncateToWordCap("Sleep and heart rate this week — and every alert"));
        Assert.Equal(
            "Sleep and heart rate this week",
            GeneratedTitles.TruncateToWordCap("Sleep and heart rate this week — and"));
    }

    [Fact]
    public void ATitleWithinTheCap_IsReturnedUntouched()
    {
        Assert.Equal("Alerts and heart rate", GeneratedTitles.TruncateToWordCap("Alerts and heart rate"));
    }
}
