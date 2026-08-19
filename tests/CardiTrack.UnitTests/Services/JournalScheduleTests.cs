using CardiTrack.Domain.Common;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// Pins the bounds a CardiJournal timing may take. The app offers only what these allow, and the
/// API refuses anything else, so a time a caregiver can pick is always one the generator honours.
/// </summary>
public class JournalScheduleTests
{
    [Fact]
    public void Null_is_selectable_and_means_the_default()
    {
        Assert.True(JournalSchedule.IsSelectableTime(null));
        Assert.Equal(new TimeOnly(2, 0), JournalSchedule.EffectiveTime(null));
        Assert.Equal(DayOfWeek.Monday, JournalSchedule.EffectiveWeekStart(null));
    }

    [Fact]
    public void A_chosen_value_wins_over_the_default()
    {
        Assert.Equal(new TimeOnly(7, 30), JournalSchedule.EffectiveTime(new TimeOnly(7, 30)));
        Assert.Equal(DayOfWeek.Sunday, JournalSchedule.EffectiveWeekStart(DayOfWeek.Sunday));
    }

    [Theory]
    [InlineData(1, 0)]   // the earliest allowed
    [InlineData(2, 0)]   // the default
    [InlineData(7, 30)]  // a waking hour
    [InlineData(12, 0)]  // the latest allowed
    public void Half_hour_times_inside_the_window_are_selectable(int hour, int minute)
        => Assert.True(JournalSchedule.IsSelectableTime(new TimeOnly(hour, minute)));

    [Theory]
    [InlineData(0, 30)]  // before the window: the day's tail is still arriving
    [InlineData(0, 0)]   // midnight, the case the default exists to avoid
    [InlineData(12, 30)] // past the window: an account of yesterday no one can act on
    [InlineData(23, 0)]
    public void Times_outside_the_window_are_refused(int hour, int minute)
        => Assert.False(JournalSchedule.IsSelectableTime(new TimeOnly(hour, minute)));

    [Theory]
    [InlineData(2, 17)]
    [InlineData(3, 1)]
    [InlineData(4, 45)]
    public void Times_off_the_half_hour_are_refused(int hour, int minute)
        => Assert.False(JournalSchedule.IsSelectableTime(new TimeOnly(hour, minute)));

    /// <summary>
    /// The digest pass runs every half hour, so a stored 02:17 would in fact be written at 02:30.
    /// Refusing it keeps the setting from quietly meaning something other than it says.
    /// </summary>
    [Fact]
    public void The_step_matches_the_job_cadence()
        => Assert.Equal(30, JournalSchedule.StepMinutes);

    [Fact]
    public void Seconds_and_milliseconds_are_refused()
    {
        Assert.False(JournalSchedule.IsSelectableTime(new TimeOnly(2, 0, 30)));
        Assert.False(JournalSchedule.IsSelectableTime(new TimeOnly(2, 0, 0, 500)));
    }
}
