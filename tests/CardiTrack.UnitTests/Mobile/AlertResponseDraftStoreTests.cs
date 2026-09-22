using CardiTrack.Mobile.Core.Alerts;
using CardiTrack.Mobile.Core.Onboarding;

namespace CardiTrack.UnitTests.Mobile;

public class AlertResponseDraftStoreTests
{
    private static readonly Guid Alert = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public async Task ADraftRoundTrips_UnderTheAccountsScope()
    {
        var keystore = new FakeSecureStore();
        var store = new AlertResponseDraftStore(keystore);
        var scope = AlertResponseDraftStore.ScopeFor("tom@example.com");

        await store.SaveAsync(scope, Alert, AlertAnswerKind.Close,
            new AlertResponseDraft { Code = "expected", Note = "Slept in" });

        var restored = await store.LoadAsync(scope, Alert, AlertAnswerKind.Close);
        Assert.NotNull(restored);
        Assert.Equal("expected", restored.Code);
        Assert.Equal("Slept in", restored.Note);
        Assert.Contains(keystore.Values.Keys, k => k.Contains(scope) && k.Contains("Close"));
    }

    [Fact]
    public async Task AnotherAccountOnTheSamePhoneSeesNothing()
    {
        var store = new AlertResponseDraftStore(new FakeSecureStore());
        await store.SaveAsync(AlertResponseDraftStore.ScopeFor("tom@example.com"), Alert,
            AlertAnswerKind.Acknowledge, new AlertResponseDraft { Note = "private" });

        var other = await store.LoadAsync(
            AlertResponseDraftStore.ScopeFor("jane@example.com"), Alert, AlertAnswerKind.Acknowledge);

        Assert.Null(other);
    }

    [Fact]
    public void TheScopeIsADigest_NotTheIdentity()
    {
        var scope = AlertResponseDraftStore.ScopeFor("Tom@Example.com ");

        Assert.DoesNotContain("example", scope);
        Assert.Equal(AlertResponseDraftStore.ScopeFor("tom@example.com"), scope);
        Assert.Equal("anonymous", AlertResponseDraftStore.ScopeFor(null));
    }

    [Fact]
    public async Task SavingAnEmptyDraftRemovesIt()
    {
        var keystore = new FakeSecureStore();
        var store = new AlertResponseDraftStore(keystore);
        var scope = AlertResponseDraftStore.ScopeFor("tom@example.com");
        await store.SaveAsync(scope, Alert, AlertAnswerKind.Acknowledge, new AlertResponseDraft { Note = "x" });

        await store.SaveAsync(scope, Alert, AlertAnswerKind.Acknowledge, new AlertResponseDraft());

        Assert.Null(await store.LoadAsync(scope, Alert, AlertAnswerKind.Acknowledge));
        Assert.Empty(keystore.Values);
    }

    [Fact]
    public async Task ClearRemovesEveryDraft_WhoeverWroteIt_AndTheIndex()
    {
        var keystore = new FakeSecureStore();
        var store = new AlertResponseDraftStore(keystore);
        await store.SaveAsync(AlertResponseDraftStore.ScopeFor("tom@example.com"), Alert,
            AlertAnswerKind.Acknowledge, new AlertResponseDraft { Note = "a" });
        await store.SaveAsync(AlertResponseDraftStore.ScopeFor("jane@example.com"), Guid.NewGuid(),
            AlertAnswerKind.Close, new AlertResponseDraft { Code = "expected" });
        Assert.Equal(3, keystore.Values.Count);

        await store.ClearAsync();

        Assert.Empty(keystore.Values);
    }

    [Fact]
    public async Task WritesLandInTheOrderTheyWereAsked_EvenWhenTheFirstIsSlow()
    {
        var keystore = new FakeSecureStore { FirstSetDelay = TimeSpan.FromMilliseconds(120) };
        var store = new AlertResponseDraftStore(keystore);
        var scope = AlertResponseDraftStore.ScopeFor("tom@example.com");

        var first = store.SaveAsync(scope, Alert, AlertAnswerKind.Acknowledge, new AlertResponseDraft { Note = "Ran" });
        var second = store.SaveAsync(scope, Alert, AlertAnswerKind.Acknowledge, new AlertResponseDraft { Note = "Rang her" });
        await Task.WhenAll(first, second);

        var restored = await store.LoadAsync(scope, Alert, AlertAnswerKind.Acknowledge);
        Assert.Equal("Rang her", restored!.Note);
    }

    [Fact]
    public async Task ARemoveQueuedAfterASave_Wins()
    {
        var keystore = new FakeSecureStore { FirstSetDelay = TimeSpan.FromMilliseconds(120) };
        var store = new AlertResponseDraftStore(keystore);
        var scope = AlertResponseDraftStore.ScopeFor("tom@example.com");

        var save = store.SaveAsync(scope, Alert, AlertAnswerKind.Close, new AlertResponseDraft { Note = "sent" });
        var remove = store.RemoveAsync(scope, Alert, AlertAnswerKind.Close);
        await Task.WhenAll(save, remove);

        Assert.Null(await store.LoadAsync(scope, Alert, AlertAnswerKind.Close));
        Assert.Empty(keystore.Values);
    }

    [Fact]
    public async Task OneFailedWriteDoesNotBlockTheNext()
    {
        var keystore = new FakeSecureStore { FailNextSet = true };
        var store = new AlertResponseDraftStore(keystore);
        var scope = AlertResponseDraftStore.ScopeFor("tom@example.com");

        await Assert.ThrowsAsync<IOException>(() =>
            store.SaveAsync(scope, Alert, AlertAnswerKind.Acknowledge, new AlertResponseDraft { Note = "lost" }));
        await store.SaveAsync(scope, Alert, AlertAnswerKind.Acknowledge, new AlertResponseDraft { Note = "kept" });

        Assert.Equal("kept", (await store.LoadAsync(scope, Alert, AlertAnswerKind.Acknowledge))!.Note);
    }

    [Fact]
    public async Task AValueWriteThatFails_LeavesNothingTheClearCannotFind()
    {
        // The index is written first, so a value write that fails leaves an index entry with
        // nothing under it — and never a value the index does not know about.
        var keystore = new FakeSecureStore { FailSetsFor = k => !k.EndsWith(":index", StringComparison.Ordinal) };
        var store = new AlertResponseDraftStore(keystore);
        var scope = AlertResponseDraftStore.ScopeFor("tom@example.com");

        await Assert.ThrowsAsync<IOException>(() =>
            store.SaveAsync(scope, Alert, AlertAnswerKind.Close, new AlertResponseDraft { Note = "orphan" }));

        Assert.DoesNotContain(keystore.Values.Keys, k => k.Contains("orphan") || k.Contains("Close"));
        Assert.Contains(keystore.Values.Keys, k => k.EndsWith(":index", StringComparison.Ordinal));

        keystore.FailSetsFor = null;
        await store.ClearAsync();
        Assert.Empty(keystore.Values);
    }

    private sealed class FakeSecureStore : ISecureKeyValueStore
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

        public TimeSpan FirstSetDelay { get; init; }

        public bool FailNextSet { get; set; }

        public Func<string, bool>? FailSetsFor { get; set; }

        private bool _delayed;

        public Task<string?> GetAsync(string key) =>
            Task.FromResult(Values.TryGetValue(key, out var v) ? v : null);

        public async Task SetAsync(string key, string value)
        {
            if (FailNextSet)
            {
                FailNextSet = false;
                throw new IOException("keystore refused");
            }

            if (FailSetsFor?.Invoke(key) == true)
                throw new IOException("keystore refused this key");

            if (!_delayed && FirstSetDelay > TimeSpan.Zero)
            {
                _delayed = true;
                await Task.Delay(FirstSetDelay);
            }

            Values[key] = value;
        }

        public void Remove(string key) => Values.Remove(key);
    }
}
