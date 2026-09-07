using CardiTrack.Mobile.Core.Forms;

namespace CardiTrack.UnitTests.Mobile;

/// <summary>
/// The rule both member forms apply to a date of birth before sending it. The case that
/// matters most is the missing one: the forms used to send today's date for a date nobody chose.
/// </summary>
public class DateOfBirthTests
{
    private static readonly DateOnly Today = new(2026, 9, 7);

    [Fact]
    public void NoDate_IsRefusedWithTheMissingMessage()
    {
        Assert.Equal(DateOfBirth.MissingMessage, DateOfBirth.Validate(null, Today));
    }

    [Fact]
    public void NoDate_IsNeverToday()
    {
        // The old fallback. Today's date is a newborn, which the age band refuses anyway — but
        // the refusal must be for the missing date, so the caregiver is told to choose one.
        Assert.NotEqual(DateOfBirth.AgeMessage, DateOfBirth.Validate(null, Today));
    }

    [Fact]
    public void AnAdult_Passes()
    {
        Assert.Null(DateOfBirth.Validate(new DateTime(1960, 8, 6), Today));
    }

    [Fact]
    public void EighteenToday_Passes()
    {
        Assert.Null(DateOfBirth.Validate(new DateTime(2008, 9, 7), Today));
    }

    [Fact]
    public void EighteenTomorrow_IsRefused()
    {
        Assert.Equal(DateOfBirth.AgeMessage, DateOfBirth.Validate(new DateTime(2008, 9, 8), Today));
    }

    [Fact]
    public void OneHundredAndTwenty_Passes()
    {
        Assert.Null(DateOfBirth.Validate(new DateTime(1906, 9, 7), Today));
    }

    [Fact]
    public void OneHundredAndTwentyOne_IsRefused()
    {
        Assert.Equal(DateOfBirth.AgeMessage, DateOfBirth.Validate(new DateTime(1905, 9, 6), Today));
    }

    [Theory]
    [InlineData(1960, 8, 6, 66)]   // birthday passed this year
    [InlineData(1960, 9, 7, 66)]   // birthday today
    [InlineData(1960, 9, 8, 65)]   // birthday tomorrow
    [InlineData(1960, 12, 31, 65)] // birthday later this year
    public void AgeCountsWholeYears_AndTheBirthdayItself(int year, int month, int day, int expected)
    {
        Assert.Equal(expected, DateOfBirth.AgeOn(new DateOnly(year, month, day), Today));
    }
}
