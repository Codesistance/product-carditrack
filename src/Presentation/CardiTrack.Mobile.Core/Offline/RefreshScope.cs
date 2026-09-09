using CardiTrack.Mobile.Core.Api;

namespace CardiTrack.Mobile.Core.Offline;

/// <summary>
/// The client calls one phase of a <see cref="SnapshotRefresh"/> run made, so their provenance
/// can be read afterwards through <see cref="ICardiTrackApiClient.OriginOf"/>. A screen that
/// loads two GETs into one render tracks both; the run then speaks for all of them, dated by
/// the oldest — the weakest thing on screen, and so the honest one to date the page by.
/// </summary>
public sealed class RefreshScope
{
    private readonly List<Task> _calls = [];

    /// <summary>Records a client call for provenance and hands the same task back.</summary>
    public Task<TCall> Track<TCall>(Task<TCall> call)
    {
        ArgumentNullException.ThrowIfNull(call);
        _calls.Add(call);
        return call;
    }

    internal bool AnyCached(ICardiTrackApiClient api)
    {
        foreach (var call in _calls)
        {
            if (api.OriginOf(call) is { WasCached: true })
                return true;
        }

        return false;
    }

    internal DateTimeOffset? OldestCachedAt(ICardiTrackApiClient api)
    {
        DateTimeOffset? oldest = null;
        foreach (var call in _calls)
        {
            if (api.OriginOf(call)?.CachedAt is { } cachedAt && (oldest is null || cachedAt < oldest))
                oldest = cachedAt;
        }

        return oldest;
    }
}
