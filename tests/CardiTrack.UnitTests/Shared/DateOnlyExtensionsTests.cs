using System.Globalization;
using CardiTrack.Domain.Extensions;

namespace CardiTrack.UnitTests.Shared;

public class DateOnlyExtensionsTests
{
    /// <summary>
    /// Invariant and exact: <c>DateOnly.Parse</c> follows the ambient culture, and one with a
    /// non-Gregorian default calendar (th-TH, ar-SA) reads "1948" as a different year entirely.
    /// </summary>
    private static DateOnly Date(string value) =>
        DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    [Theory]
    [InlineData("1948-03-15", "2026-03-15", 78)]  // on the birthday
    [InlineData("1948-03-15", "2026-03-14", 77)]  // the day before — the case a naive year subtraction gets wrong
    [InlineData("1948-03-15", "2026-03-16", 78)]
    public void ToAgeInYears_CountsCompletedYears(string birth, string asOf, int expected)
    {
        Assert.Equal(expected, Date(birth).ToAgeInYears(Date(asOf)));
    }

    [Fact]
    public void ToAgeInYears_TreatsALeapDayBirthdayAs28February()
    {
        // .NET clamps 29 February to the 28th when adding years, so a leapling counts as a year
        // older from 28 February rather than the 1 March used by the UK statutory rule. The
        // difference is one day on one date, and this age only labels a dashboard and a prompt.
        Assert.Equal(78, new DateOnly(1948, 2, 29).ToAgeInYears(new DateOnly(2026, 2, 28)));
        Assert.Equal(77, new DateOnly(1948, 2, 29).ToAgeInYears(new DateOnly(2026, 2, 27)));
    }
}
