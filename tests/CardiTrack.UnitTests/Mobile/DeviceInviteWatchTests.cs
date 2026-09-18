using CardiTrack.Mobile.Core.Devices;

namespace CardiTrack.UnitTests.Mobile;

/// <summary>
/// The caregiver's waiting screen, minus the screen. Everything here is a decision the page makes
/// about somebody else's progress — which status means what, when to ask again, when a deadline has
/// gone — and none of it needs a device to be worth checking.
/// </summary>
public class DeviceInviteWatchTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    private static DeviceInviteWatch Watch(int expiresInMinutes, out MovableClock clock)
    {
        clock = new MovableClock(Now);
        return new DeviceInviteWatch(Now.UtcDateTime.AddMinutes(expiresInMinutes), clock);
    }

    [Fact]
    public void StartsWaiting_AndPollsSlowlyUntilTheyOpenIt()
    {
        var watch = Watch(15, out _);

        Assert.Equal(DeviceInviteWatchState.Waiting, watch.State);
        Assert.False(watch.IsFinished);
        Assert.Equal(DeviceInviteWatch.IdlePollInterval, watch.NextPollDelay);
    }

    [Fact]
    public void PollsFaster_OnceTheyHaveOpenedIt()
    {
        var watch = Watch(15, out _);

        Assert.True(watch.Apply("opened"));

        // From here the next change is the one the caregiver is waiting for, and it can arrive at
        // any moment.
        Assert.Equal(DeviceInviteWatch.ActivePollInterval, watch.NextPollDelay);
        Assert.True(DeviceInviteWatch.ActivePollInterval < DeviceInviteWatch.IdlePollInterval);
    }

    [Theory]
    [InlineData("completed", DeviceInviteWatchState.Connected)]
    [InlineData("declined", DeviceInviteWatchState.Declined)]
    [InlineData("revoked", DeviceInviteWatchState.Cancelled)]
    [InlineData("expired", DeviceInviteWatchState.Expired)]
    public void TerminalStatuses_StopThePolling(string status, DeviceInviteWatchState expected)
    {
        var watch = Watch(15, out _);

        watch.Apply(status);

        Assert.Equal(expected, watch.State);
        Assert.True(watch.IsFinished);
        Assert.Null(watch.NextPollDelay);
    }

    [Fact]
    public void RecordsTheConnection_WhenTheyFinish()
    {
        var deviceId = Guid.NewGuid();
        var watch = Watch(15, out _);

        watch.Apply("completed", deviceId);

        Assert.Equal(DeviceInviteWatchState.Connected, watch.State);
        Assert.Equal(deviceId, watch.DeviceId);
    }

    [Fact]
    public void ALatePoll_CannotUndoATerminalState()
    {
        var deviceId = Guid.NewGuid();
        var watch = Watch(15, out _);
        watch.Apply("completed", deviceId);

        // A poll already in flight when they finished lands afterwards carrying the older answer.
        // Letting it through would flicker the screen back to a QR code nobody should scan again.
        Assert.False(watch.Apply("opened"));
        Assert.Equal(DeviceInviteWatchState.Connected, watch.State);
        Assert.Equal(deviceId, watch.DeviceId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("something_this_build_has_never_heard_of")]
    public void AnUnrecognisedStatus_ChangesNothing(string? status)
    {
        var watch = Watch(15, out _);
        watch.Apply("opened");

        // A newer server is not a reason to tell the caregiver their invitation failed.
        Assert.False(watch.Apply(status));
        Assert.Equal(DeviceInviteWatchState.Opened, watch.State);
    }

    [Fact]
    public void Remaining_NeverGoesNegative()
    {
        var watch = Watch(15, out var clock);

        clock.Advance(TimeSpan.FromMinutes(20));

        // The countdown is rendered straight onto the screen; a negative value would show there.
        Assert.Equal(TimeSpan.Zero, watch.Remaining);
    }

    [Fact]
    public void ExpiresLocally_OnlyOnceTheDeadlineHasActuallyPassed()
    {
        var watch = Watch(15, out var clock);

        Assert.False(watch.ExpireIfElapsed());
        Assert.Equal(DeviceInviteWatchState.Waiting, watch.State);

        clock.Advance(TimeSpan.FromMinutes(16));

        // The fallback for a server we cannot reach: the deadline has demonstrably gone, so the
        // screen stops spinning rather than waiting forever.
        Assert.True(watch.ExpireIfElapsed());
        Assert.Equal(DeviceInviteWatchState.Expired, watch.State);
    }

    [Fact]
    public void ExpiringLocally_NeverOverwritesAnAnswerWeAlreadyHave()
    {
        var watch = Watch(15, out var clock);
        watch.Apply("completed", Guid.NewGuid());

        clock.Advance(TimeSpan.FromMinutes(20));

        // The grant landed before the deadline. A clock that has since passed it must not turn a
        // connected device into an expired invitation.
        Assert.False(watch.ExpireIfElapsed());
        Assert.Equal(DeviceInviteWatchState.Connected, watch.State);
    }

    [Fact]
    public void TheServersExpiredStatus_IsTakenOverOurOwnClock()
    {
        // Expiry is the server's to declare: its clock is the one the invitation was minted
        // against, and a phone whose time is adrift must not disagree with it.
        var watch = Watch(15, out _);

        Assert.True(watch.Apply("expired"));
        Assert.Equal(DeviceInviteWatchState.Expired, watch.State);
        Assert.True(watch.Remaining > TimeSpan.Zero);
    }

    [Fact]
    public void Cancelling_IsTerminal_AndBeatsALaterPoll()
    {
        var watch = Watch(15, out _);

        Assert.True(watch.Cancel());
        Assert.Equal(DeviceInviteWatchState.Cancelled, watch.State);

        Assert.False(watch.Apply("opened"));
        Assert.Equal(DeviceInviteWatchState.Cancelled, watch.State);
    }

    [Fact]
    public void Cancelling_DoesNothing_OnceItHasFinished()
    {
        var watch = Watch(15, out _);
        watch.Apply("completed", Guid.NewGuid());

        Assert.False(watch.Cancel());
        Assert.Equal(DeviceInviteWatchState.Connected, watch.State);
    }

    private sealed class MovableClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }
}
