using CardiTrack.Mobile.Core.Offline;

namespace CardiTrack.UnitTests.Mobile;

public sealed class OfflineStatusLineStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Window = TimeSpan.FromHours(6);

    private readonly Guid _member = Guid.NewGuid();
    private readonly MemoryOfflineCache _cache = new();
    private readonly FakeClock _clock = new(Now);

    private OfflineStatusLineStore CreateSut() => new(_cache, _clock);

    private static StoredStatusLine Line(
        string healthStatus = "green",
        string? headline = "All steady",
        string message = "Margaret is doing well.",
        DateTimeOffset? generatedAt = null) =>
        new(healthStatus, headline, message, generatedAt ?? Now);

    [Fact]
    public async Task SaveThenGet_RoundTripsTheLine()
    {
        var sut = CreateSut();
        await sut.SaveAsync(_member, Line());

        var restored = await sut.TryGetAsync(_member, "green", Window);

        Assert.NotNull(restored);
        Assert.Equal("green", restored!.HealthStatus);
        Assert.Equal("All steady", restored.Headline);
        Assert.Equal("Margaret is doing well.", restored.Message);
        Assert.Equal(Now, restored.GeneratedAt);
    }

    [Fact]
    public async Task SaveThenGet_KeepsANullHeadline()
    {
        var sut = CreateSut();
        await sut.SaveAsync(_member, Line(headline: null));

        var restored = await sut.TryGetAsync(_member, "green", Window);

        Assert.NotNull(restored);
        Assert.Null(restored!.Headline);
    }

    /// <summary>
    /// The safety case. The server sends no tier back with the message, so a line written about a
    /// steady day must never be put back on a card that has since moved to a worse one.
    /// </summary>
    [Fact]
    public async Task TryGet_ReturnsNull_WhenTheTierHasChanged()
    {
        var sut = CreateSut();
        await sut.SaveAsync(_member, Line("green"));

        Assert.Null(await sut.TryGetAsync(_member, "orange", Window));
    }

    [Fact]
    public async Task TryGet_ReturnsTheLine_JustInsideTheWindow()
    {
        var sut = CreateSut();
        await sut.SaveAsync(_member, Line());

        _clock.Advance(Window - TimeSpan.FromMinutes(1));

        Assert.NotNull(await sut.TryGetAsync(_member, "green", Window));
    }

    [Fact]
    public async Task TryGet_ReturnsNull_JustOutsideTheWindow()
    {
        var sut = CreateSut();
        await sut.SaveAsync(_member, Line());

        _clock.Advance(Window + TimeSpan.FromMinutes(1));

        Assert.Null(await sut.TryGetAsync(_member, "green", Window));
    }

    /// <summary>
    /// The age is measured from when the server generated the line, not from when it was written
    /// here — a response served from the API's own cache is already older than this write.
    /// </summary>
    [Fact]
    public async Task TryGet_MeasuresAgeFromGeneratedAt_NotFromTheWrite()
    {
        var sut = CreateSut();
        await sut.SaveAsync(_member, Line(generatedAt: Now - Window - TimeSpan.FromMinutes(1)));

        Assert.Null(await sut.TryGetAsync(_member, "green", Window));
    }

    [Fact]
    public async Task TryGet_ReturnsNull_ForADifferentMember()
    {
        var sut = CreateSut();
        await sut.SaveAsync(_member, Line());

        Assert.Null(await sut.TryGetAsync(Guid.NewGuid(), "green", Window));
    }

    [Fact]
    public async Task TryGet_ReturnsNull_WhenNothingWasSaved()
    {
        Assert.Null(await CreateSut().TryGetAsync(_member, "green", Window));
    }

    [Fact]
    public async Task Save_ReplacesThePreviousLine()
    {
        var sut = CreateSut();
        await sut.SaveAsync(_member, Line(message: "First."));
        await sut.SaveAsync(_member, Line(message: "Second."));

        var restored = await sut.TryGetAsync(_member, "green", Window);

        Assert.Equal("Second.", restored!.Message);
        Assert.Single(_cache.Items);
    }

    [Fact]
    public async Task Clear_DropsTheLine_AndLeavesOtherMembersAlone()
    {
        var other = Guid.NewGuid();
        var sut = CreateSut();
        await sut.SaveAsync(_member, Line());
        await sut.SaveAsync(other, Line());

        await sut.ClearAsync(_member);

        Assert.Null(await sut.TryGetAsync(_member, "green", Window));
        Assert.NotNull(await sut.TryGetAsync(other, "green", Window));
    }

    [Fact]
    public async Task TryGet_ReturnsNull_AndDropsAnUnreadablePayload()
    {
        var sut = CreateSut();
        await sut.SaveAsync(_member, Line());
        _cache.Overwrite(_cache.Items.Keys.Single(), "not json at all");

        Assert.Null(await sut.TryGetAsync(_member, "green", Window));
        // Dropped rather than left to fail the same way on every load until it expires.
        Assert.Empty(_cache.Items);
    }

    [Fact]
    public async Task TryGet_ReturnsNull_WhenThePayloadHasNoMessage()
    {
        var sut = CreateSut();
        await sut.SaveAsync(_member, Line());
        _cache.Overwrite(
            _cache.Items.Keys.Single(),
            """{"healthStatus":"green","headline":"All steady","message":"","generatedAt":"2026-08-17T12:00:00+00:00"}""");

        Assert.Null(await sut.TryGetAsync(_member, "green", Window));
    }

    [Fact]
    public async Task Save_DoesNotThrow_WhenTheCacheIsUnavailable()
    {
        _cache.FailEverything();

        await CreateSut().SaveAsync(_member, Line());
    }

    [Fact]
    public async Task TryGet_ReturnsNull_WhenTheCacheIsUnavailable()
    {
        _cache.FailEverything();

        Assert.Null(await CreateSut().TryGetAsync(_member, "green", Window));
    }

    [Fact]
    public async Task Clear_DoesNotThrow_WhenTheCacheIsUnavailable()
    {
        _cache.FailEverything();

        await CreateSut().ClearAsync(_member);
    }

    private sealed class MemoryOfflineCache : IOfflineReadCache
    {
        private bool _fail;

        public Dictionary<string, OfflineCacheEntry> Items { get; } = new(StringComparer.Ordinal);

        public void FailEverything() => _fail = true;

        public void Overwrite(string key, string payload) =>
            Items[key] = new OfflineCacheEntry(payload, Items[key].CachedAt);

        public Task SaveAsync(string key, string payload, CancellationToken ct = default)
        {
            Throw();
            Items[key] = new OfflineCacheEntry(payload, Now);
            return Task.CompletedTask;
        }

        public Task<OfflineCacheEntry?> TryGetAsync(string key, CancellationToken ct = default)
        {
            Throw();
            return Task.FromResult(Items.TryGetValue(key, out var entry) ? entry : null);
        }

        public Task RemoveAsync(string key, CancellationToken ct = default)
        {
            Throw();
            Items.Remove(key);
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken ct = default)
        {
            Throw();
            Items.Clear();
            return Task.CompletedTask;
        }

        private void Throw()
        {
            if (_fail)
                throw new InvalidOperationException("cache unavailable");
        }
    }

    private sealed class FakeClock(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _now = utcNow;

        public void Advance(TimeSpan by) => _now = _now.Add(by);

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
