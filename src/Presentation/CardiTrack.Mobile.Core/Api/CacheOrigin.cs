namespace CardiTrack.Mobile.Core.Api;

/// <summary>
/// Where one GET's payload came from: the live API, or the on-device cache because the live
/// call could not reach it.
/// </summary>
/// <remarks>
/// One of these belongs to one call, and is reached through
/// <see cref="ICardiTrackApiClient.OriginOf"/> with the very task that call returned. That is the
/// whole point of the type. This used to be a pair of properties on the client — the origin of
/// "the last GET" — which is a fact about a shared singleton and not about the screen reading it:
/// the client is resolved once for the whole app, every GET reset the pair on entry, and a screen
/// read it an await later. Five screens refresh together when the app is resumed, and a single
/// screen can have two GETs in flight at once, so the pair a screen read routinely belonged to
/// somebody else's call — which is how the offline banner came up over data that had just arrived
/// fresh off the network, and stayed down over data that had not.
/// </remarks>
public sealed class CacheOrigin
{
    /// <summary>When the cached snapshot was written, or null if the call reached the API.</summary>
    public DateTimeOffset? CachedAt { get; internal set; }

    /// <summary>Whether this call was answered from the on-device cache.</summary>
    public bool WasCached => CachedAt is not null;
}
