using CardiTrack.Mobile.Core.Onboarding;

namespace CardiTrack.UnitTests.Mobile;

public class CardiMemberDraftStoreTests
{
    private static readonly DateTime Now = new(2026, 8, 7, 12, 0, 0, DateTimeKind.Utc);

    private readonly FakeSecureStore _store = new();
    private readonly FakePhotoStore _photos = new();
    private readonly FakeClock _clock = new(Now);

    private CardiMemberDraftStore CreateSut() => new(_store, _photos, _clock);

    private static CardiMemberDraft Filled() => new()
    {
        Name = "Margaret",
        DateOfBirth = new DateTime(1948, 3, 2),
        RelationshipIndex = 0,
        SexIndex = 1,
        DetailsExpanded = true,
        MedicalNotes = "Warfarin, mornings",
        EmergencyContactName = "Dr Patel",
        EmergencyContactPhone = "+441632960111",
    };

    [Fact]
    public async Task Load_ReturnsNull_WhenNothingSaved()
    {
        Assert.Null(await CreateSut().LoadAsync());
    }

    [Fact]
    public async Task SaveThenLoad_RoundTripsEveryField()
    {
        var sut = CreateSut();
        await sut.SaveAsync(Filled());

        var restored = await sut.LoadAsync();

        Assert.NotNull(restored);
        Assert.Equal("Margaret", restored!.Name);
        Assert.Equal(new DateTime(1948, 3, 2), restored.DateOfBirth);
        Assert.Equal(0, restored.RelationshipIndex);
        Assert.Equal(1, restored.SexIndex);
        Assert.True(restored.DetailsExpanded);
        Assert.Equal("Warfarin, mornings", restored.MedicalNotes);
        Assert.Equal("Dr Patel", restored.EmergencyContactName);
        Assert.Equal("+441632960111", restored.EmergencyContactPhone);
    }

    [Fact]
    public async Task Save_StampsTheSaveTimeFromTheClock()
    {
        var sut = CreateSut();
        var draft = Filled();

        await sut.SaveAsync(draft);

        Assert.Equal(Now, draft.SavedUtc);
        Assert.Equal(Now, (await sut.LoadAsync())!.SavedUtc);
    }

    [Fact]
    public async Task Load_DropsTheDraft_OnceOlderThanTheLifetime()
    {
        var sut = CreateSut();
        await sut.SaveAsync(Filled());

        _clock.Advance(CardiMemberDraftStore.Lifetime + TimeSpan.FromMinutes(1));

        Assert.Null(await sut.LoadAsync());
        Assert.False(_store.HasAnything);
    }

    [Fact]
    public async Task Load_KeepsTheDraft_JustInsideTheLifetime()
    {
        var sut = CreateSut();
        await sut.SaveAsync(Filled());

        _clock.Advance(CardiMemberDraftStore.Lifetime - TimeSpan.FromMinutes(1));

        Assert.NotNull(await sut.LoadAsync());
    }

    [Fact]
    public async Task Load_ClearsThePhotoPath_WhenTheFileIsGone()
    {
        var sut = CreateSut();
        var draft = Filled();
        draft.PhotoPath = "/data/gone.img";
        await sut.SaveAsync(draft);

        var restored = await sut.LoadAsync();

        Assert.NotNull(restored);
        Assert.Null(restored!.PhotoPath);
        Assert.Equal("Margaret", restored.Name); // the rest of the draft survives
    }

    [Fact]
    public async Task Load_KeepsThePhotoPath_WhenTheFileIsStillThere()
    {
        var sut = CreateSut();
        var draft = Filled();
        draft.PhotoPath = _photos.Add("/data/photo.img");
        await sut.SaveAsync(draft);

        Assert.Equal("/data/photo.img", (await sut.LoadAsync())!.PhotoPath);
    }

    [Fact]
    public async Task Load_ReturnsNull_WhenOnlyThePhotoWasSavedAndItIsGone()
    {
        // A photo-only draft whose file vanished has nothing left worth restoring.
        var sut = CreateSut();
        await sut.SaveAsync(new CardiMemberDraft { PhotoPath = "/data/gone.img" });

        Assert.Null(await sut.LoadAsync());
    }

    [Fact]
    public async Task Save_ClearsInsteadOfWriting_WhenTheFormIsEmpty()
    {
        var sut = CreateSut();
        await sut.SaveAsync(Filled());

        // Expanding the optional-details section isn't input.
        await sut.SaveAsync(new CardiMemberDraft { DetailsExpanded = true });

        Assert.False(_store.HasAnything);
        Assert.Null(await sut.LoadAsync());
    }

    [Fact]
    public async Task Clear_RemovesThePayloadAndItsPhoto()
    {
        var sut = CreateSut();
        var draft = Filled();
        draft.PhotoPath = _photos.Add("/data/photo.img");
        await sut.SaveAsync(draft);

        await sut.ClearAsync();

        Assert.False(_store.HasAnything);
        Assert.False(_photos.Exists("/data/photo.img"));
    }

    [Fact]
    public async Task Load_ReturnsNull_WhenTheStoredPayloadIsCorrupt()
    {
        _store.Poison("not json");

        Assert.Null(await CreateSut().LoadAsync());
    }

    [Fact]
    public async Task Load_ReturnsNull_WhenTheSecureStoreIsUnavailable()
    {
        _store.FailEverything();

        Assert.Null(await CreateSut().LoadAsync());
    }

    [Fact]
    public async Task Save_DoesNotThrow_WhenTheSecureStoreIsUnavailable()
    {
        _store.FailEverything();

        // The draft is dropped rather than written somewhere unencrypted.
        await CreateSut().SaveAsync(Filled());
    }

    [Fact]
    public async Task Clear_DoesNotThrow_WhenTheSecureStoreIsUnavailable()
    {
        _store.FailEverything();

        await CreateSut().ClearAsync();
    }

    [Fact]
    public async Task CapturePhoto_StoresTheNewPhotoAndDropsTheOne_ItReplaces()
    {
        var sut = CreateSut();
        var previous = _photos.Add("/data/first.img");

        var path = await sut.CapturePhotoAsync(new MemoryStream([1, 2, 3]), previous);

        Assert.NotNull(path);
        Assert.NotEqual(previous, path);
        Assert.True(_photos.Exists(path!));
        Assert.False(_photos.Exists(previous));
    }

    [Fact]
    public async Task CapturePhoto_KeepsThePreviousPhoto_WhenTheCopyFails()
    {
        var sut = CreateSut();
        var previous = _photos.Add("/data/first.img");
        _photos.FailSaves();

        var path = await sut.CapturePhotoAsync(new MemoryStream([1, 2, 3]), previous);

        Assert.Null(path);
        Assert.True(_photos.Exists(previous));
    }

    [Theory]
    [InlineData(nameof(CardiMemberDraft.Name))]
    [InlineData(nameof(CardiMemberDraft.DateOfBirth))]
    [InlineData(nameof(CardiMemberDraft.RelationshipIndex))]
    [InlineData(nameof(CardiMemberDraft.SexIndex))]
    [InlineData(nameof(CardiMemberDraft.MedicalNotes))]
    [InlineData(nameof(CardiMemberDraft.EmergencyContactName))]
    [InlineData(nameof(CardiMemberDraft.EmergencyContactPhone))]
    [InlineData(nameof(CardiMemberDraft.PhotoPath))]
    public void HasContent_IsTrue_ForAnySingleFilledField(string field)
    {
        var draft = new CardiMemberDraft();
        switch (field)
        {
            case nameof(CardiMemberDraft.Name): draft.Name = "Margaret"; break;
            case nameof(CardiMemberDraft.DateOfBirth): draft.DateOfBirth = new DateTime(1948, 3, 2); break;
            case nameof(CardiMemberDraft.RelationshipIndex): draft.RelationshipIndex = 0; break;
            case nameof(CardiMemberDraft.SexIndex): draft.SexIndex = 0; break;
            case nameof(CardiMemberDraft.MedicalNotes): draft.MedicalNotes = "Warfarin"; break;
            case nameof(CardiMemberDraft.EmergencyContactName): draft.EmergencyContactName = "Dr Patel"; break;
            case nameof(CardiMemberDraft.EmergencyContactPhone): draft.EmergencyContactPhone = "+44"; break;
            case nameof(CardiMemberDraft.PhotoPath): draft.PhotoPath = "/data/photo.img"; break;
        }

        Assert.True(draft.HasContent);
    }

    [Fact]
    public void HasContent_IsFalse_ForAnUntouchedForm()
    {
        // -1 is "no relationship picked"; whitespace is not input.
        Assert.False(new CardiMemberDraft().HasContent);
        Assert.False(new CardiMemberDraft { DetailsExpanded = true, Name = "   " }.HasContent);
    }

    private sealed class FakeSecureStore : ISecureKeyValueStore
    {
        private readonly Dictionary<string, string> _values = [];
        private bool _fail;

        public bool HasAnything => _values.Count > 0;

        public void Poison(string raw) => _values["onboarding.cardimember_draft"] = raw;

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

    private sealed class FakePhotoStore : IDraftPhotoStore
    {
        private readonly HashSet<string> _files = [];
        private int _saves;
        private bool _fail;

        public string Add(string path)
        {
            _files.Add(path);
            return path;
        }

        public void FailSaves() => _fail = true;

        public bool Exists(string path) => _files.Contains(path);

        public Task<string?> SaveAsync(Stream content)
        {
            if (_fail)
                throw new IOException("disk full");
            var path = $"/data/captured-{++_saves}.img";
            _files.Add(path);
            return Task.FromResult<string?>(path);
        }

        public void Delete(string path) => _files.Remove(path);
    }

    private sealed class FakeClock(DateTime utcNow) : TimeProvider
    {
        private DateTimeOffset _now = new(utcNow, TimeSpan.Zero);

        public void Advance(TimeSpan by) => _now = _now.Add(by);

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
