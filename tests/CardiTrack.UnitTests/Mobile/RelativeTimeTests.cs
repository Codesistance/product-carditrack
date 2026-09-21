using CardiTrack.Mobile.Core.Forms;

namespace CardiTrack.UnitTests.Mobile;

/// <summary>
/// How long ago something happened, in words — the caption under a sync, an alert, an answered
/// question, and now a health background nobody has confirmed.
/// </summary>
/// <remarks>
/// The unit coarsens as the span grows, and that is the whole of what these pin. "412 days ago" is
/// an arithmetic problem rather than an answer, and the surfaces that can legitimately be old are
/// exactly the ones where the reader is trying to judge whether to act.
/// </remarks>
public class RelativeTimeTests
{
    private static string Ago(TimeSpan elapsed) => RelativeTime.Format(DateTime.UtcNow - elapsed);

    [Fact]
    public void SomethingJustHappened_ReadsAsJustNow() =>
        Assert.Equal("just now", Ago(TimeSpan.FromSeconds(20)));

    [Theory]
    [InlineData(1, "1 minute ago")]
    [InlineData(10, "10 minutes ago")]
    [InlineData(59, "59 minutes ago")]
    public void MinutesAreCountedSingly(int minutes, string expected) =>
        Assert.Equal(expected, Ago(TimeSpan.FromMinutes(minutes)));

    [Theory]
    [InlineData(1, "1 hour ago")]
    [InlineData(5, "5 hours ago")]
    [InlineData(23, "23 hours ago")]
    public void HoursAreCountedSingly(int hours, string expected) =>
        Assert.Equal(expected, Ago(TimeSpan.FromHours(hours)));

    [Theory]
    [InlineData(1, "1 day ago")]
    [InlineData(3, "3 days ago")]
    [InlineData(59, "59 days ago")]
    public void DaysAreCountedUpToTwoMonths(int days, string expected) =>
        Assert.Equal(expected, Ago(TimeSpan.FromDays(days)));

    /// <summary>
    /// The boundary the existing surfaces sit below: everything under two months reads exactly as
    /// it did before the longer tail existed, so a sync caption or an alert time cannot have moved.
    /// </summary>
    [Fact]
    public void TheDayCountRunsRightUpToTheMonthBoundary()
    {
        Assert.Equal("59 days ago", Ago(TimeSpan.FromDays(59)));
        Assert.Equal("1 month ago", Ago(TimeSpan.FromDays(60)));
    }

    /// <summary>
    /// And the month count runs right up to the year. Eleven months must not become "1 year ago"
    /// early, and twelve must not linger as "12 months ago".
    /// </summary>
    [Fact]
    public void TheMonthCountRunsRightUpToTheYearBoundary()
    {
        Assert.Equal("11 months ago", Ago(TimeSpan.FromDays(364)));
        Assert.Equal("1 year ago", Ago(TimeSpan.FromDays(365)));
    }

    [Theory]
    [InlineData(60, "1 month ago")]
    [InlineData(95, "3 months ago")]
    [InlineData(183, "6 months ago")]
    [InlineData(300, "9 months ago")]
    public void LongerSpansAreCountedInMonths(int days, string expected) =>
        Assert.Equal(expected, Ago(TimeSpan.FromDays(days)));

    /// <summary>
    /// Two months of days divides to 1 at an average month length, and "1 month ago" for something
    /// nine weeks old is wrong in the direction that matters: it reads as more recent than it is.
    /// </summary>
    [Fact]
    public void TwoMonthsNeverReadsAsOne() =>
        Assert.Equal("1 month ago", Ago(TimeSpan.FromDays(60)));

    [Theory]
    [InlineData(730, "2 years ago")]
    [InlineData(1100, "3 years ago")]
    public void YearsTakeOverOnceMonthsStopMeaningAnything(int days, string expected) =>
        Assert.Equal(expected, Ago(TimeSpan.FromDays(days)));

    /// <summary>
    /// A clock that disagrees with the server's — a device set ahead, or a row stamped a moment in
    /// the future by a fast writer — must not render as a negative age or a span in the future.
    /// </summary>
    [Fact]
    public void ATimestampInTheFuture_ReadsAsJustNow() =>
        Assert.Equal("just now", RelativeTime.Format(DateTime.UtcNow.AddHours(2)));

    /// <summary>
    /// The stored value is UTC but carries Unspecified kind through some paths; read as local it
    /// would be out by the offset, which on this side of the world is a whole hour of wrongness.
    /// </summary>
    [Fact]
    public void AnUnspecifiedKindIsReadAsUtc()
    {
        var utcNow = DateTime.UtcNow;
        var unspecified = new DateTime(utcNow.Ticks, DateTimeKind.Unspecified);

        Assert.Equal("just now", RelativeTime.Format(unspecified));
    }
}
