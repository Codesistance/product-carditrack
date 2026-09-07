using CardiTrack.Mobile.Core.Forms;

namespace CardiTrack.UnitTests.Mobile;

/// <summary>
/// The clamping the app's own date field does, pinned to the platform DatePicker's so the date of
/// birth and export range forms behave as they did before the control changed hands.
/// </summary>
public class DateBoundsTests
{
    private static readonly DateTime Earliest = new(2020, 1, 1);
    private static readonly DateTime Latest = new(2020, 12, 31);

    [Fact]
    public void ADateInsideTheBounds_IsKept()
    {
        var chosen = new DateTime(2020, 6, 15);

        Assert.Equal(chosen, DateBounds.Clamp(chosen, Earliest, Latest));
    }

    [Fact]
    public void ADateBeforeTheEarliest_SettlesOnTheEarliest()
    {
        Assert.Equal(Earliest, DateBounds.Clamp(new DateTime(2019, 3, 3), Earliest, Latest));
    }

    [Fact]
    public void ADateAfterTheLatest_SettlesOnTheLatest()
    {
        Assert.Equal(Latest, DateBounds.Clamp(new DateTime(2021, 3, 3), Earliest, Latest));
    }

    [Fact]
    public void NothingStaysNothing()
    {
        Assert.Null(DateBounds.Clamp(null, Earliest, Latest));
    }

    [Fact]
    public void OnlyTheDayIsKept()
    {
        var withTime = new DateTime(2020, 6, 15, 13, 45, 0);

        Assert.Equal(new DateTime(2020, 6, 15), DateBounds.Clamp(withTime, Earliest, Latest));
    }

    [Fact]
    public void TheBoundsAreReadAsDaysToo()
    {
        // A bound carrying a time of day must not push a date on that same day out of range.
        var latestAtNoon = new DateTime(2020, 12, 31, 12, 0, 0);

        Assert.Equal(new DateTime(2020, 12, 31), DateBounds.Clamp(new DateTime(2020, 12, 31), Earliest, latestAtNoon));
    }

    [Fact]
    public void AnEarliestAboveTheLatest_SettlesOnTheLatest()
    {
        var upsideDown = DateBounds.Clamp(new DateTime(2020, 6, 15), Latest, Earliest);

        Assert.Equal(Earliest, upsideDown);
    }

    [Fact]
    public void TheDefaultsAreThePlatformControls()
    {
        Assert.Equal(new DateTime(1900, 1, 1), DateBounds.DefaultMinimum);
        Assert.Equal(new DateTime(2100, 12, 31), DateBounds.DefaultMaximum);
    }
}
