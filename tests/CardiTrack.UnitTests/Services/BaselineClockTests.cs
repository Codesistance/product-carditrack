using CardiTrack.Application.Services;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// A time of day is a point on a circle, not a number on a line. Everything here is a case where
/// treating it as the latter gives a confidently wrong answer that reads perfectly plausibly.
/// </summary>
public class BaselineClockTests
{
    // ── The short way round ─────────────────────────────────────────────────────

    /// <summary>
    /// The case issue #492's follow-up is about: a night that crosses midnight. Naive subtraction
    /// calls 23:50 against a usual of 00:10 twenty-three hours and forty minutes late; it is
    /// twenty minutes early.
    /// </summary>
    [Fact]
    public void MinutesFrom_ReadsABedtimeBeforeMidnight_AsEarlyAgainstAUsualAfterIt()
    {
        Assert.Equal(-20, BaselineClock.MinutesFrom(new TimeOnly(23, 50), new TimeOnly(0, 10)));
    }

    /// <summary>And the same crossing the other way.</summary>
    [Fact]
    public void MinutesFrom_ReadsABedtimeAfterMidnight_AsLateAgainstAUsualBeforeIt()
    {
        Assert.Equal(20, BaselineClock.MinutesFrom(new TimeOnly(0, 10), new TimeOnly(23, 50)));
    }

    [Theory]
    [InlineData(23, 14, 22, 30, 44)]      // ordinary late night, no crossing
    [InlineData(7, 2, 6, 45, 17)]         // ordinary late wake
    [InlineData(6, 30, 7, 0, -30)]        // ordinary early wake
    [InlineData(22, 30, 22, 30, 0)]       // exactly the usual
    public void MinutesFrom_SignsTheGap_LaterPositiveEarlierNegative(
        int hour, int minute, int usualHour, int usualMinute, int expected)
    {
        Assert.Equal(
            expected,
            BaselineClock.MinutesFrom(new TimeOnly(hour, minute), new TimeOnly(usualHour, usualMinute)));
    }

    /// <summary>
    /// Never more than half a day either way — past that the short way round has turned back, which
    /// is the property the Daybook's direction bound relies on to stay reachable.
    /// </summary>
    [Fact]
    public void MinutesFrom_NeverExceedsHalfADay_InEitherDirection()
    {
        for (var minutes = 0; minutes < 1440; minutes++)
        {
            var actual = TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(minutes));
            var gap = BaselineClock.MinutesFrom(actual, new TimeOnly(22, 30));

            Assert.InRange(gap, -719, 720);
        }
    }

    /// <summary>
    /// The far end, where earlier and later stop being different claims: twelve hours apart is one
    /// answer the arithmetic has to pick, and it picks late so the function stays total.
    /// </summary>
    [Fact]
    public void MinutesFrom_ResolvesTheAntipode_ToASingleAnswer()
    {
        Assert.Equal(720, BaselineClock.MinutesFrom(new TimeOnly(10, 30), new TimeOnly(22, 30)));
        Assert.Equal(720, BaselineClock.MinutesFrom(new TimeOnly(22, 30), new TimeOnly(10, 30)));
    }

    // ── Onto the member's clock ─────────────────────────────────────────────────

    [Fact]
    public void Local_PutsABaselineTimeOfDay_OnTheMembersWallClock()
    {
        var newYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

        // 02:40 UTC in January is 21:40 the evening before in New York — an evening the model
        // cannot see if it is handed the UTC face.
        var local = BaselineClock.Local(new TimeOnly(2, 40), new DateOnly(2026, 1, 15), newYork);

        Assert.Equal(new TimeOnly(21, 40), local);
    }

    /// <summary>
    /// Anchored to a date rather than a fixed offset, so a baseline read either side of a
    /// daylight-saving change lands on the clock the household actually kept.
    /// </summary>
    [Fact]
    public void Local_ReadsTheSameStoredFace_DifferentlyAcrossADaylightSavingChange()
    {
        var london = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

        var winter = BaselineClock.Local(new TimeOnly(22, 40), new DateOnly(2026, 1, 15), london);
        var summer = BaselineClock.Local(new TimeOnly(22, 40), new DateOnly(2026, 7, 15), london);

        Assert.Equal(new TimeOnly(22, 40), winter);
        Assert.Equal(new TimeOnly(23, 40), summer);
    }

    [Fact]
    public void Local_LeavesTheStoredFaceAlone_WithoutAZone()
    {
        Assert.Equal(
            new TimeOnly(22, 40),
            BaselineClock.Local(new TimeOnly(22, 40), new DateOnly(2026, 1, 15), timeZone: null));
    }

    [Fact]
    public void Local_KeepsNullNull()
    {
        Assert.Null(BaselineClock.Local((TimeOnly?)null, new DateOnly(2026, 1, 15), TimeZoneInfo.Utc));
        Assert.Null(BaselineClock.Local((DateTime?)null, TimeZoneInfo.Utc));
    }

    [Fact]
    public void Local_PutsAStoredInstant_OnTheMembersWallClock()
    {
        var newYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var instant = new DateTime(2026, 1, 16, 2, 40, 0, DateTimeKind.Utc);

        Assert.Equal(new TimeOnly(21, 40), BaselineClock.Local(instant, newYork));
    }
}
