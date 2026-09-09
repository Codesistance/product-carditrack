using CardiTrack.Mobile.Core.Offline;

namespace CardiTrack.UnitTests.Mobile;

public class LoadGateTests
{
    [Fact]
    public void Begin_CancelsThePreviousTicket_AndIssuesANewerOne()
    {
        var gate = new LoadGate();

        var first = gate.Begin();
        var second = gate.Begin();

        Assert.True(first.Token.IsCancellationRequested);
        Assert.False(second.Token.IsCancellationRequested);
        Assert.True(second.Generation > first.Generation);
    }

    [Fact]
    public void IsCurrent_IsFalse_ForASupersededTicket_AndTrueForTheNewest()
    {
        var gate = new LoadGate();

        var first = gate.Begin();
        var second = gate.Begin();

        Assert.False(gate.IsCurrent(first));
        Assert.True(gate.IsCurrent(second));
    }

    [Fact]
    public void IsLoading_TracksBeginAndRelease()
    {
        var gate = new LoadGate();
        Assert.False(gate.IsLoading);

        var ticket = gate.Begin();
        Assert.True(gate.IsLoading);

        gate.Release(ticket);
        Assert.False(gate.IsLoading);
    }

    [Fact]
    public void Release_TwiceIsSafe()
    {
        var gate = new LoadGate();
        var ticket = gate.Begin();

        gate.Release(ticket);
        gate.Release(ticket);

        Assert.True(gate.IsCurrent(ticket));
    }

    [Fact]
    public void Release_OfAStaleTicket_LeavesTheNewerLoadRunning()
    {
        var gate = new LoadGate();
        var first = gate.Begin();
        var second = gate.Begin();

        // The loser's finally runs after the winner has begun — it must not take the winner's
        // source down with it.
        gate.Release(first);

        Assert.True(gate.IsLoading);
        Assert.True(gate.IsCurrent(second));
        Assert.False(second.Token.IsCancellationRequested);
    }

    [Fact]
    public void CancelInFlight_AfterRelease_IsANoOp()
    {
        var gate = new LoadGate();
        var ticket = gate.Begin();
        gate.Release(ticket);

        gate.CancelInFlight();

        Assert.False(gate.IsLoading);
    }
}
