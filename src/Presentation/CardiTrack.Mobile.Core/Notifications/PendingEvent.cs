namespace CardiTrack.Mobile.Core.Notifications;

/// <summary>
/// One event that can fire before anyone is listening. The first subscriber is replayed the
/// pending args, so a killed-process notification tap is not discarded because AppShell had
/// not subscribed yet.
/// </summary>
public sealed class PendingEvent<TEventArgs>
{
    private readonly object _gate = new();
    private EventHandler<TEventArgs>? _handlers;
    private object? _pendingSender;
    private TEventArgs? _pending;
    private bool _hasPending;

    public void Subscribe(EventHandler<TEventArgs> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        object? sender = null;
        TEventArgs? pending = default;
        var replay = false;
        lock (_gate)
        {
            _handlers += handler;
            if (_hasPending)
            {
                sender = _pendingSender;
                pending = _pending;
                replay = true;
                _hasPending = false;
                _pendingSender = null;
                _pending = default;
            }
        }

        if (replay)
            handler(sender, pending!);
    }

    public void Unsubscribe(EventHandler<TEventArgs> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
            _handlers -= handler;
    }

    public void Discard()
    {
        lock (_gate)
        {
            _hasPending = false;
            _pendingSender = null;
            _pending = default;
        }
    }

    public void Raise(object? sender, TEventArgs args)
    {
        EventHandler<TEventArgs>? handlers;
        lock (_gate)
        {
            handlers = _handlers;
            if (handlers is null)
            {
                _pendingSender = sender;
                _pending = args;
                _hasPending = true;
                return;
            }
        }

        handlers(sender, args);
    }
}
