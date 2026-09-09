namespace CardiTrack.Mobile.Core.Offline;

/// <summary>
/// One screen's "which load is the current one" guard. A screen that renders twice per load —
/// the saved snapshot, then the live answer — has two chances for a superseded load to paint
/// over a newer one, so every await is followed by <see cref="IsCurrent"/> and the loser
/// drops its result on the floor.
/// </summary>
/// <remarks>
/// Lifted from the alerts list, which was the only screen with this discipline (#308: a chip
/// tap while a slow load was in flight let the old filter's rows land under the new chip).
/// The generation counter is what makes the check cheap; the token is what stops the loser
/// spending any more network on an answer nobody will read.
/// </remarks>
public sealed class LoadGate
{
    private int _generation;
    private CancellationTokenSource? _cts;

    /// <summary>Whether a load begun through this gate has not yet been released.</summary>
    public bool IsLoading => _cts is not null;

    /// <summary>
    /// Supersedes whatever is in flight and hands back the ticket for the new load. Never
    /// skips: anything the caregiver asked for by hand must not be swallowed because a slow
    /// load happens to be running. Callers that want "skip if busy" check
    /// <see cref="IsLoading"/> first.
    /// </summary>
    public LoadTicket Begin()
    {
        CancelInFlight();
        var cts = new CancellationTokenSource();
        _cts = cts;
        return new LoadTicket(++_generation, cts.Token);
    }

    /// <summary>True while <paramref name="ticket"/> is the newest load and has not been cancelled.</summary>
    public bool IsCurrent(LoadTicket ticket) =>
        ticket.Generation == _generation && !ticket.Token.IsCancellationRequested;

    /// <summary>
    /// Marks the ticket's load finished. A stale ticket releases nothing: the newer load owns
    /// the gate now, and disposing its source from the loser's <c>finally</c> is exactly the
    /// fault that used to abort the new load before it drew anything (#307, #308).
    /// </summary>
    public void Release(LoadTicket ticket)
    {
        if (ticket.Generation != _generation)
            return;

        var cts = _cts;
        _cts = null;
        cts?.Dispose();
    }

    /// <summary>Cancels the current load, if any, without starting another.</summary>
    public void CancelInFlight()
    {
        var cts = _cts;
        _cts = null;
        if (cts is null)
            return;

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already released by its own finally; nothing left to stop.
        }

        cts.Dispose();
    }
}

/// <summary>The identity of one load: its place in the sequence, and the token that ends it.</summary>
public readonly record struct LoadTicket(int Generation, CancellationToken Token);
