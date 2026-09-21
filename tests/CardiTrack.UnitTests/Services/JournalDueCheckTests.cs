using CardiTrack.Domain.Common;
using CardiTrack.Domain.Entities;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// The due rule the CardiJournal books and the journal-aligned trend horizons now share. These
/// pin it in one place for the reason it was extracted: two copies of a per-member,
/// timezone-sensitive rule drift, and the drift would not crash — it would be a trend narrative
/// dated to a different week than the Weekbook beside it, each describing seven days the other
/// did not.
/// </summary>
public class JournalDueCheckTests
{
    private static CardiMember Member(
        DayOfWeek? weekStart = null, TimeOnly? weekbookTime = null, TimeOnly? monthbookTime = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            Name = "Margaret Doe",
            JournalWeekStartsOn = weekStart,
            WeekbookLocalTime = weekbookTime,
            MonthbookLocalTime = monthbookTime,
        };

    // 2026-09-07 is a Monday, which is JournalSchedule.DefaultWeekStartsOn.
    private static readonly DateTime MondayAtThree = new(2026, 9, 7, 3, 0, 0, DateTimeKind.Unspecified);

    [Fact]
    public void AWeekIsDueOnTheWeekStart_OnceTheChosenHourHasPassed()
    {
        var period = JournalDueCheck.Weekly(Member(), MondayAtThree);

        Assert.NotNull(period);
        // The week that ended last night, dated by its last day — so one date identifies one week.
        Assert.Equal(new DateOnly(2026, 9, 6), period!.Value.End);
        Assert.Equal(new DateOnly(2026, 8, 31), period.Value.Start);
        Assert.Equal(7, period.Value.DayCount);
    }

    [Fact]
    public void AWeekIsNotDueBeforeTheChosenHour()
    {
        // The default write time is 02:00; this member asked for 06:00 and it is 03:00.
        var member = Member(weekbookTime: new TimeOnly(6, 0));

        Assert.Null(JournalDueCheck.Weekly(member, MondayAtThree));
    }

    [Fact]
    public void AWeekIsNotDueOnAnyOtherWeekday()
    {
        // Same instant, a member whose week starts on Thursday.
        var member = Member(weekStart: DayOfWeek.Thursday);

        Assert.Null(JournalDueCheck.Weekly(member, MondayAtThree));
    }

    [Fact]
    public void AMemberWhoChoseTheirOwnWeekStartIsDueOnThatDay()
    {
        // 2026-09-07 is a Monday, so a Monday-starting member is due and this one is too when the
        // clock reaches their Thursday.
        var member = Member(weekStart: DayOfWeek.Thursday);
        var thursday = new DateTime(2026, 9, 10, 3, 0, 0, DateTimeKind.Unspecified);

        var period = JournalDueCheck.Weekly(member, thursday);

        Assert.NotNull(period);
        Assert.Equal(new DateOnly(2026, 9, 9), period!.Value.End);
        Assert.Equal(new DateOnly(2026, 9, 3), period.Value.Start);
    }

    [Fact]
    public void AMonthIsDueOnTheFirst_AndCoversTheMonthJustGone()
    {
        var period = JournalDueCheck.Monthly(
            Member(), new DateTime(2026, 9, 1, 3, 0, 0, DateTimeKind.Unspecified));

        Assert.NotNull(period);
        Assert.Equal(new DateOnly(2026, 8, 1), period!.Value.Start);
        Assert.Equal(new DateOnly(2026, 8, 31), period.Value.End);
        Assert.Equal(31, period.Value.DayCount);
    }

    [Fact]
    public void AMonthIsNotDueOnAnyOtherDay()
    {
        Assert.Null(JournalDueCheck.Monthly(
            Member(), new DateTime(2026, 9, 2, 3, 0, 0, DateTimeKind.Unspecified)));
    }

    [Fact]
    public void AMonthIsNotDueBeforeTheChosenHour()
    {
        var member = Member(monthbookTime: new TimeOnly(6, 0));

        Assert.Null(JournalDueCheck.Monthly(
            member, new DateTime(2026, 9, 1, 3, 0, 0, DateTimeKind.Unspecified)));
    }

    [Fact]
    public void AShortMonthIsStillTheWholeMonth()
    {
        // February's twenty-eight days, so DayCount cannot be assumed to be thirty-one.
        var period = JournalDueCheck.Monthly(
            Member(), new DateTime(2026, 3, 1, 3, 0, 0, DateTimeKind.Unspecified));

        Assert.NotNull(period);
        Assert.Equal(new DateOnly(2026, 2, 1), period!.Value.Start);
        Assert.Equal(new DateOnly(2026, 2, 28), period.Value.End);
        Assert.Equal(28, period.Value.DayCount);
    }

    [Theory]
    // The guard spans UTC-12 to UTC+14, so from any instant it reaches back twelve hours and
    // forward fourteen. Midday on the 31st already touches the 1st somewhere...
    [InlineData(2026, 8, 31, 12, 0, true)]
    // ...and early on the 2nd it still reaches back to the 1st.
    [InlineData(2026, 9, 2, 2, 0, true)]
    [InlineData(2026, 9, 1, 12, 0, true)]
    // But midnight on the 31st reaches only the 30th and the 31st, and mid-month reaches neither
    // end of any month — the case that spares the pass on about twenty-nine days in thirty.
    [InlineData(2026, 8, 31, 0, 30, false)]
    [InlineData(2026, 9, 15, 12, 0, false)]
    public void TheCheapMonthlyGuardIsGenerousAtBothEnds(
        int year, int month, int day, int hour, int minute, bool possible)
    {
        var utc = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Utc);

        Assert.Equal(possible, JournalDueCheck.AnyTimeZoneCouldBeOnDayOfMonth(utc, 1));
    }
}
