using CardiTrack.Mobile.Core.Notifications;

namespace CardiTrack.UnitTests.Notifications;

public class PendingEventTests
{
    private static readonly NudgeDestination Settings =
        new(NudgeDestinationKind.Settings, null);

    [Fact]
    public void Raise_BeforeSubscribe_ReplaysToTheFirstHandler()
    {
        var pending = new PendingEvent<NudgeDestination>();
        NudgeDestination? seen = null;

        pending.Raise(this, Settings);
        pending.Subscribe((_, destination) => seen = destination);

        Assert.Equal(Settings, seen);
    }

    [Fact]
    public void Raise_AfterSubscribe_InvokesImmediately()
    {
        var pending = new PendingEvent<NudgeDestination>();
        NudgeDestination? seen = null;
        pending.Subscribe((_, destination) => seen = destination);

        pending.Raise(this, Settings);

        Assert.Equal(Settings, seen);
    }

    [Fact]
    public void Unsubscribe_StopsDelivery()
    {
        var pending = new PendingEvent<NudgeDestination>();
        var count = 0;
        void Handler(object? _, NudgeDestination destination) => count++;
        pending.Subscribe(Handler);
        pending.Unsubscribe(Handler);

        pending.Raise(this, Settings);

        Assert.Equal(0, count);
    }

    [Fact]
    public void Discard_DropsAPendingRaise()
    {
        var pending = new PendingEvent<NudgeDestination>();
        NudgeDestination? seen = null;
        pending.Raise(this, Settings);
        pending.Discard();
        pending.Subscribe((_, destination) => seen = destination);

        Assert.Null(seen);
    }

    [Fact]
    public void Replay_HappensOnlyOnce()
    {
        var pending = new PendingEvent<NudgeDestination>();
        var first = 0;
        var second = 0;
        pending.Raise(this, Settings);
        pending.Subscribe((_, _) => first++);
        pending.Subscribe((_, _) => second++);

        Assert.Equal(1, first);
        Assert.Equal(0, second);
    }
}
