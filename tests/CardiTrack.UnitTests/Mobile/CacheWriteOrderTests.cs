using CardiTrack.Mobile.Core.Offline;

namespace CardiTrack.UnitTests.Mobile;

/// <summary>
/// The order cached answers land in. Two reads of the same path are routinely out at once and
/// nothing makes them come back in the order they were sent, so an older body must not be the one
/// left on the device (#1105).
/// </summary>
public class CacheWriteOrderTests
{
    private const string Key = "api/v1/insights/members/x/digest";

    [Fact]
    public async Task A_read_writes_what_it_fetched()
    {
        var order = new CacheWriteOrder();
        var written = new List<string>();

        var read = order.Begin();
        await order.WriteAsync(Key, read, () => Record(written, "first"));

        Assert.Equal(["first"], written);
    }

    /// <summary>The bug itself: the older read comes back last and must not be the one kept.</summary>
    [Fact]
    public async Task An_older_read_does_not_write_over_a_newer_one()
    {
        var order = new CacheWriteOrder();
        var written = new List<string>();

        var older = order.Begin();
        var newer = order.Begin();

        await order.WriteAsync(Key, newer, () => Record(written, "newer"));
        await order.WriteAsync(Key, older, () => Record(written, "older"));

        Assert.Equal(["newer"], written);
    }

    /// <summary>
    /// And the guard only holds an older answer off: the next read of that key writes as usual, or
    /// one overlap would freeze the key for the life of the app.
    /// </summary>
    [Fact]
    public async Task The_next_read_writes_again()
    {
        var order = new CacheWriteOrder();
        var written = new List<string>();

        var older = order.Begin();
        var newer = order.Begin();
        await order.WriteAsync(Key, newer, () => Record(written, "newer"));
        await order.WriteAsync(Key, older, () => Record(written, "older"));

        await order.WriteAsync(Key, order.Begin(), () => Record(written, "later still"));

        Assert.Equal(["newer", "later still"], written);
    }

    [Fact]
    public async Task One_key_says_nothing_about_another()
    {
        var order = new CacheWriteOrder();
        var written = new List<string>();

        var older = order.Begin();
        await order.WriteAsync(Key, order.Begin(), () => Record(written, "newer"));

        await order.WriteAsync("api/v1/insights/members/x/advise", older, () => Record(written, "other key"));

        Assert.Equal(["newer", "other key"], written);
    }

    /// <summary>
    /// The reason this is a shared object rather than a field on the API client: the typed client
    /// is transient, so the two screens most likely to be reading the same member hold different
    /// ones, and an order kept per client would be no order at all between them.
    /// </summary>
    [Fact]
    public async Task Reads_from_different_holders_share_one_order()
    {
        var order = new CacheWriteOrder();
        var written = new List<string>();

        // One screen's client starts its read, another screen's client starts a later one.
        var onePage = order.Begin();
        var anotherPage = order.Begin();

        await order.WriteAsync(Key, anotherPage, () => Record(written, "the newer page"));
        await order.WriteAsync(Key, onePage, () => Record(written, "the older page"));

        Assert.Equal(["the newer page"], written);
    }

    /// <summary>
    /// The write is held per key while it runs, so a claim cannot be taken between the check and
    /// the write and finish after it — a narrower window is not an order.
    /// </summary>
    [Fact]
    public async Task A_slow_write_is_not_overtaken_midway()
    {
        var order = new CacheWriteOrder();
        var written = new List<string>();
        var slowIsIn = new TaskCompletionSource();
        var releaseSlow = new TaskCompletionSource();

        var older = order.Begin();
        var newer = order.Begin();

        var slow = order.WriteAsync(Key, newer, async () =>
        {
            slowIsIn.SetResult();
            await releaseSlow.Task;
            lock (written)
                written.Add("newer");
        });

        await slowIsIn.Task;
        var overtaking = order.WriteAsync(Key, older, () => Record(written, "older"));

        releaseSlow.SetResult();
        await Task.WhenAll(slow, overtaking);

        Assert.Equal(["newer"], written);
    }

    private static Task Record(List<string> written, string what)
    {
        lock (written)
            written.Add(what);
        return Task.CompletedTask;
    }
}
