using System.Text;
using CardiTrack.Mobile.Core.Onboarding;

namespace CardiTrack.UnitTests.Mobile;

public sealed class FileDraftPhotoStoreTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"carditrack-draft-tests-{Guid.NewGuid():N}");

    private FileDraftPhotoStore CreateSut() => new(_directory);

    private static MemoryStream Content(string text) => new(Encoding.UTF8.GetBytes(text));

    [Fact]
    public async Task Save_WritesTheContentAndReturnsItsPath()
    {
        var path = await CreateSut().SaveAsync(Content("photo bytes"));

        Assert.NotNull(path);
        Assert.True(File.Exists(path));
        Assert.Equal("photo bytes", await File.ReadAllTextAsync(path!));
    }

    [Fact]
    public async Task Save_CreatesTheDirectory_WhenItIsNotThereYet()
    {
        Assert.False(Directory.Exists(_directory));

        var path = await CreateSut().SaveAsync(Content("photo bytes"));

        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task Save_UsesAFreshNameEachTime()
    {
        // Image sources cache by path — a reused filename would redisplay the old photo.
        var sut = CreateSut();

        var first = await sut.SaveAsync(Content("first"));
        var second = await sut.SaveAsync(Content("second"));

        Assert.NotEqual(first, second);
        Assert.Equal("first", await File.ReadAllTextAsync(first!));
        Assert.Equal("second", await File.ReadAllTextAsync(second!));
    }

    [Fact]
    public async Task Save_KeepsTheFileInsideTheNominatedDirectory()
    {
        var path = await CreateSut().SaveAsync(Content("photo bytes"));

        Assert.Equal(_directory, Path.GetDirectoryName(path));
    }

    [Fact]
    public async Task Exists_TracksTheFile()
    {
        var sut = CreateSut();
        var path = await sut.SaveAsync(Content("photo bytes"));

        Assert.True(sut.Exists(path!));

        File.Delete(path!);

        Assert.False(sut.Exists(path!));
    }

    [Fact]
    public void Exists_IsFalse_ForAPathThatWasNeverThere()
    {
        Assert.False(CreateSut().Exists(Path.Combine(_directory, "nothing.img")));
    }

    [Fact]
    public async Task Delete_RemovesTheFile()
    {
        var sut = CreateSut();
        var path = await sut.SaveAsync(Content("photo bytes"));

        sut.Delete(path!);

        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Delete_IsSilent_WhenTheFileIsAlreadyGone()
    {
        // The draft store deletes the photo it replaces without checking first.
        CreateSut().Delete(Path.Combine(_directory, "already-gone.img"));
    }

    [Fact]
    public async Task RoundTripsThroughTheDraftStore()
    {
        // The pairing that matters on device: capture a photo, then have the store decide
        // the draft still points at something real.
        var photos = CreateSut();
        var drafts = new CardiMemberDraftStore(new InMemorySecureStore(), photos);

        var path = await drafts.CapturePhotoAsync(Content("photo bytes"), previousPath: null);
        await drafts.SaveAsync(new CardiMemberDraft { Name = "Margaret", PhotoPath = path });

        Assert.Equal(path, (await drafts.LoadAsync())!.PhotoPath);

        File.Delete(path!);

        Assert.Null((await drafts.LoadAsync())!.PhotoPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private sealed class InMemorySecureStore : ISecureKeyValueStore
    {
        private readonly Dictionary<string, string> _values = [];

        public Task<string?> GetAsync(string key) =>
            Task.FromResult(_values.TryGetValue(key, out var value) ? value : null);

        public Task SetAsync(string key, string value)
        {
            _values[key] = value;
            return Task.CompletedTask;
        }

        public void Remove(string key) => _values.Remove(key);
    }
}
