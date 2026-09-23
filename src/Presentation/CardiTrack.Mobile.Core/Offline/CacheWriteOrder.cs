namespace CardiTrack.Mobile.Core.Offline;

/// <summary>
/// Keeps the device's cached answers in the order they were asked for: a read that started
/// earlier never writes over a key a later one has already written.
/// </summary>
/// <remarks>
/// <para>
/// Two reads of the same path are routinely out at once — a screen pulled down while its first
/// read is still in flight, two pages of the same CardiMember, a screen and the cache warmer —
/// and nothing makes the answers come back in the order they were sent. A screen can drop the
/// loser before it draws it (<see cref="FollowUpGate"/>), but the cache is written underneath
/// that and read by whoever peeks next, so an older body landing last outlives the page that
/// fetched it.
/// </para>
/// <para>
/// Ordered by when a read started, because that is what says which answer is older: the server
/// only moves forward, so a request sent later cannot be describing an earlier state.
/// </para>
/// <para>
/// Shared rather than held by the client, and that is the whole reason this is a type of its own.
/// <c>AddHttpClient</c> registers the typed client as transient, so every screen holds its own
/// <c>CardiTrackApiClient</c> while they all write one cache — an order kept per client would be
/// no order at all between the two pages most likely to be reading the same member. Registered
/// as a singleton beside <c>SessionGeneration</c>, which is shared for the same kind of reason.
/// </para>
/// <para>
/// One writer at a time per key, because claiming a key and then writing it unheld would still
/// let a slow write finish last — a narrower window, not an order.
/// </para>
/// </remarks>
public sealed class CacheWriteOrder
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _wroteLast =
        new(StringComparer.Ordinal);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _writers =
        new(StringComparer.Ordinal);

    private int _reads;

    /// <summary>
    /// This read's place in the order. Taken before the request goes out, never zero, and only
    /// ever increasing.
    /// </summary>
    public int Begin() => Interlocked.Increment(ref _reads);

    /// <summary>
    /// Runs <paramref name="write"/> for <paramref name="key"/> unless a read that started later
    /// has already written it.
    /// </summary>
    /// <remarks>
    /// The key is marked as written whether or not the write itself got that far: all that mark
    /// can do is keep an older body out, and an older body is no more welcome for the newer one
    /// having been refused on the way in.
    /// The wait takes no cancellation token deliberately — it is bounded by one cache write, and
    /// cancelling here would turn a skipped save into a fault thrown out of a call whose answer
    /// has already arrived.
    /// </remarks>
    public async Task WriteAsync(string key, int read, Func<Task> write)
    {
        ArgumentNullException.ThrowIfNull(write);

        var writer = _writers.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await writer.WaitAsync(CancellationToken.None);
        try
        {
            if (_wroteLast.TryGetValue(key, out var wroteLast) && wroteLast > read)
                return;

            await write();
            _wroteLast[key] = read;
        }
        finally
        {
            writer.Release();
        }
    }
}
