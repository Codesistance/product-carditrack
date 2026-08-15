using System.Text;
using CardiTrack.Mobile.Core.Offline;
using CardiTrack.Mobile.Core.Onboarding;

namespace CardiTrack.UnitTests.Mobile;

public sealed class EncryptedFileOfflineReadCacheTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "ct-offline-" + Guid.NewGuid().ToString("N"));

    private readonly FakeSecureStore _secure = new();
    private readonly FakeClock _clock = new(new DateTime(2026, 8, 15, 12, 0, 0, DateTimeKind.Utc));

    private EncryptedFileOfflineReadCache CreateSut() =>
        new(_directory, _secure, _clock);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task SaveThenGet_RoundTripsThePayload()
    {
        var sut = CreateSut();
        await sut.SaveAsync("api/v1/dashboard", """{"name":"Margaret"}""");

        var entry = await sut.TryGetAsync("api/v1/dashboard");

        Assert.NotNull(entry);
        Assert.Equal("""{"name":"Margaret"}""", entry!.Payload);
        Assert.Equal(_clock.GetUtcNow(), entry.CachedAt);
    }

    [Fact]
    public async Task FilesOnDisk_DoNotContainThePayloadInTheClear()
    {
        var sut = CreateSut();
        await sut.SaveAsync("api/v1/dashboard", """{"name":"Margaret","restingHeartRate":72}""");

        var files = Directory.GetFiles(_directory, "*.bin");
        Assert.Single(files);
        var text = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(files[0]));
        Assert.DoesNotContain("Margaret", text);
        Assert.DoesNotContain("restingHeartRate", text);
    }

    [Fact]
    public async Task TryGet_ReturnsNull_WhenNothingWasSaved()
    {
        Assert.Null(await CreateSut().TryGetAsync("missing"));
    }

    [Fact]
    public async Task TryGet_ReturnsNull_OnceTheEntryIsOlderThanTheLifetime()
    {
        var sut = CreateSut();
        await sut.SaveAsync("api/v1/dashboard", "{}");

        _clock.Advance(IOfflineReadCache.Lifetime + TimeSpan.FromMinutes(1));

        Assert.Null(await sut.TryGetAsync("api/v1/dashboard"));
    }

    [Fact]
    public async Task TryGet_KeepsTheEntry_JustInsideTheLifetime()
    {
        var sut = CreateSut();
        await sut.SaveAsync("api/v1/dashboard", "{}");

        _clock.Advance(IOfflineReadCache.Lifetime - TimeSpan.FromMinutes(1));

        Assert.NotNull(await sut.TryGetAsync("api/v1/dashboard"));
    }

    [Fact]
    public async Task TryGet_ReturnsNull_WhenTheFileHasBeenTamperedWith()
    {
        var sut = CreateSut();
        await sut.SaveAsync("api/v1/dashboard", """{"name":"Margaret"}""");

        var file = Directory.GetFiles(_directory, "*.bin").Single();
        var bytes = await File.ReadAllBytesAsync(file);
        bytes[^1] ^= 0xFF;
        await File.WriteAllBytesAsync(file, bytes);

        Assert.Null(await sut.TryGetAsync("api/v1/dashboard"));
    }

    [Fact]
    public async Task Save_RotatesACorruptDek_RatherThanDisablingTheCache()
    {
        _secure.Poison("offline-cache.dek", "not-valid-base64!!");
        var sut = CreateSut();

        await sut.SaveAsync("api/v1/dashboard", """{"name":"Margaret"}""");

        var entry = await sut.TryGetAsync("api/v1/dashboard");
        Assert.NotNull(entry);
        Assert.Equal("""{"name":"Margaret"}""", entry!.Payload);
    }

    [Fact]
    public async Task Save_IsANoOp_WhenSecureStorageIsUnavailable()
    {
        _secure.FailEverything();
        var sut = CreateSut();

        await sut.SaveAsync("api/v1/dashboard", """{"name":"Margaret"}""");

        Assert.False(Directory.Exists(_directory));
        Assert.Null(await sut.TryGetAsync("api/v1/dashboard"));
    }

    [Fact]
    public async Task Clear_DropsEntries_AndRotatesTheKey()
    {
        var sut = CreateSut();
        await sut.SaveAsync("api/v1/dashboard", "{}");
        Assert.True(_secure.HasAnything);

        await sut.ClearAsync();

        Assert.False(Directory.Exists(_directory));
        Assert.False(_secure.HasAnything);
        Assert.Null(await sut.TryGetAsync("api/v1/dashboard"));
    }

    private sealed class FakeSecureStore : ISecureKeyValueStore
    {
        private readonly Dictionary<string, string> _values = [];
        private bool _fail;

        public bool HasAnything => _values.Count > 0;

        public void Poison(string key, string raw) => _values[key] = raw;

        public void FailEverything() => _fail = true;

        public Task<string?> GetAsync(string key)
        {
            if (_fail)
                throw new InvalidOperationException("secure storage unavailable");
            return Task.FromResult(_values.TryGetValue(key, out var value) ? value : null);
        }

        public Task SetAsync(string key, string value)
        {
            if (_fail)
                throw new InvalidOperationException("secure storage unavailable");
            _values[key] = value;
            return Task.CompletedTask;
        }

        public void Remove(string key)
        {
            if (_fail)
                throw new InvalidOperationException("secure storage unavailable");
            _values.Remove(key);
        }
    }

    private sealed class FakeClock(DateTime utcNow) : TimeProvider
    {
        private DateTimeOffset _now = new(utcNow, TimeSpan.Zero);

        public void Advance(TimeSpan by) => _now = _now.Add(by);

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
