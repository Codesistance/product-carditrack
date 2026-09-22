using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CardiTrack.Mobile.Core.Onboarding;

namespace CardiTrack.Mobile.Core.Alerts;

/// <summary>
/// Where an unsent alert response lives while the caregiver is away from the page: the device's
/// keystore, under the signed-in account, with every write applied in the order it was asked for.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Scoped to the account.</strong> A key carries a digest of the caregiver's identity as
/// well as the alert and the kind of answer, so on a shared phone the next caregiver who can open
/// the same alert does not find the last one's half-written note waiting in the field. Encryption
/// at rest says nothing about who may read a value back; the scope does.
/// </para>
/// <para>
/// <strong>Cleared on sign-out.</strong> The keystore cannot list its own entries, so this keeps
/// an index of the keys it has written and <see cref="ClearAsync"/> removes every one of them,
/// whatever account wrote them — the same discipline the member draft and the offline cache
/// follow when a session ends.
/// </para>
/// <para>
/// <strong>Writes are serialised.</strong> The page saves on every keystroke and does not wait,
/// so two writes for one key are routinely in flight together. They are chained, one after the
/// other, so the last one asked for is the last one applied — an older payload cannot land on top
/// of a newer note, and a save cannot finish after the remove that followed it and resurrect a
/// response that was already sent.
/// </para>
/// </remarks>
public sealed class AlertResponseDraftStore
{
    private const string Prefix = "AlertResponseDraft";
    private const string IndexKey = Prefix + ":index";

    private readonly ISecureKeyValueStore _store;
    private readonly object _gate = new();
    private Task _chain = Task.CompletedTask;

    public AlertResponseDraftStore(ISecureKeyValueStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>
    /// The account's share of the key: a digest of whatever identifies the signed-in caregiver,
    /// never the identity itself, since the key names sit in the keystore's own index.
    /// </summary>
    public static string ScopeFor(string? identity)
    {
        var normalised = (identity ?? string.Empty).Trim().ToLowerInvariant();
        if (normalised.Length == 0)
            return "anonymous";

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(normalised));
        return Convert.ToHexString(digest.AsSpan(0, 8)).ToLowerInvariant();
    }

    /// <summary>Per account, per alert and per kind: a half-written close must not reappear under Acknowledge.</summary>
    public static string Key(string scope, Guid alertId, AlertAnswerKind kind) =>
        $"{Prefix}:{scope}:{alertId:N}:{kind}";

    public async Task<AlertResponseDraft?> LoadAsync(string scope, Guid alertId, AlertAnswerKind kind)
    {
        // Behind whatever writes are queued, so a load issued just after a save reads the save.
        await Quiesce();
        return AlertResponseDraft.Deserialize(await _store.GetAsync(Key(scope, alertId, kind)));
    }

    /// <summary>
    /// Saves the draft, or removes it when there is nothing in it. Queued behind every earlier
    /// write; the returned task completes when this one has been applied.
    /// </summary>
    public Task SaveAsync(string scope, Guid alertId, AlertAnswerKind kind, AlertResponseDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var key = Key(scope, alertId, kind);
        // Serialised now, not when the write runs: the page goes on mutating the draft it handed
        // over, and a payload captured later would be a different keystroke's.
        var payload = draft.IsEmpty ? null : draft.Serialize();
        return Enqueue(() => WriteAsync(key, payload));
    }

    public Task RemoveAsync(string scope, Guid alertId, AlertAnswerKind kind) =>
        Enqueue(() => WriteAsync(Key(scope, alertId, kind), null));

    /// <summary>Every draft this store has written, for any account. Sign-out's call.</summary>
    public Task ClearAsync() => Enqueue(async () =>
    {
        foreach (var key in await ReadIndexAsync())
            _store.Remove(key);
        _store.Remove(IndexKey);
    });

    /// <summary>
    /// Two writes that cannot be one, ordered so a failure between them errs towards the index
    /// knowing too much rather than too little.
    /// </summary>
    /// <remarks>
    /// Saving: the index first, then the value. If the value write then fails, the index names a
    /// key with nothing under it, which <see cref="ClearAsync"/> removes harmlessly. The other
    /// order left the value in the keystore with no index entry, and a sign-out that walked the
    /// index would never find it — an unsent note about the wearer, kept for the next caregiver on
    /// the phone. Removing: the value first, then the index, for the same reason.
    /// </remarks>
    private async Task WriteAsync(string key, string? payload)
    {
        var index = await ReadIndexAsync();
        if (payload is null)
        {
            _store.Remove(key);
            if (index.Remove(key))
                await WriteIndexAsync(index);
            return;
        }

        if (index.Add(key))
            await WriteIndexAsync(index);
        await _store.SetAsync(key, payload);
    }

    private async Task<HashSet<string>> ReadIndexAsync()
    {
        var stored = await _store.GetAsync(IndexKey);
        if (string.IsNullOrWhiteSpace(stored))
            return new HashSet<string>(StringComparer.Ordinal);
        try
        {
            return new HashSet<string>(
                JsonSerializer.Deserialize<string[]>(stored) ?? [], StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            // An index that will not read is an index worth rebuilding from nothing: the entries
            // it named are still removable by the caller that knows their keys, and a clear
            // that trusted a broken index would remove nothing at all.
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    private Task WriteIndexAsync(HashSet<string> index) =>
        index.Count == 0
            ? Task.Run(() => _store.Remove(IndexKey))
            : _store.SetAsync(IndexKey, JsonSerializer.Serialize(index.ToArray()));

    /// <summary>
    /// Appends <paramref name="work"/> to the chain. A failure is the caller's to observe through
    /// the returned task and does not stop the writes queued behind it.
    /// </summary>
    private Task Enqueue(Func<Task> work)
    {
        lock (_gate)
        {
            var next = _chain.ContinueWith(
                _ => work(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default).Unwrap();
            // The chain itself never faults: the next link waits on a completed antecedent
            // whether or not this one threw.
            _chain = next.ContinueWith(_ => { }, TaskScheduler.Default);
            return next;
        }
    }

    private Task Quiesce()
    {
        lock (_gate)
            return _chain;
    }
}
