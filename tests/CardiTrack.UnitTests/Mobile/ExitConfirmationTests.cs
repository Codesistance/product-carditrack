using CardiTrack.Mobile.Core.Navigation;

namespace CardiTrack.UnitTests.Mobile;

public class ExitConfirmationTests
{
    [Fact]
    public void First_press_does_not_exit()
    {
        var sut = new ExitConfirmation(Frozen.At(DateTimeOffset.UnixEpoch));

        Assert.False(sut.Confirm());
        Assert.True(sut.IsArmed);
    }

    [Fact]
    public void Second_press_within_the_window_exits()
    {
        var clock = Frozen.At(DateTimeOffset.UnixEpoch);
        var sut = new ExitConfirmation(clock);

        sut.Confirm();
        clock.UtcNow += TimeSpan.FromSeconds(1);

        Assert.True(sut.Confirm());
        Assert.False(sut.IsArmed);
    }

    [Fact]
    public void Second_press_after_the_window_is_a_new_first_press()
    {
        var clock = Frozen.At(DateTimeOffset.UnixEpoch);
        var sut = new ExitConfirmation(clock);

        sut.Confirm();
        clock.UtcNow += ExitConfirmation.Window + TimeSpan.FromMilliseconds(1);

        // The window lapsed — this arms again rather than closing the app on a press the
        // caregiver no longer has a reason to think is a confirmation.
        Assert.False(sut.Confirm());
        Assert.True(sut.IsArmed);

        clock.UtcNow += TimeSpan.FromSeconds(1);
        Assert.True(sut.Confirm());
    }

    [Fact]
    public void Disarm_cancels_a_pending_confirmation()
    {
        var clock = Frozen.At(DateTimeOffset.UnixEpoch);
        var sut = new ExitConfirmation(clock);

        sut.Confirm();
        sut.Disarm();

        Assert.False(sut.IsArmed);
        Assert.False(sut.Confirm());
    }

    private sealed class Frozen : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; }

        public static Frozen At(DateTimeOffset utcNow) => new() { UtcNow = utcNow };

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
