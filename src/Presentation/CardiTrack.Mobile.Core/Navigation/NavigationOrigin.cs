namespace CardiTrack.Mobile.Core.Navigation;

/// <summary>
/// Where the caregiver was when a control sent them to a tab, so back can put them back.
/// </summary>
/// <remarks>
/// <para>
/// An absolute route — "//alerts" — selects a tab and drops everything above it. That is what a
/// control naming its destination should do, and it is also how the caregiver's place gets lost:
/// tapping the bell on Member Detail replaced a stack of two with a tab root, and back from a tab
/// root has nothing to pop, so Android finished the app instead of returning. From the caregiver's
/// side an app that closes is indistinguishable from a crash.
/// </para>
/// <para>
/// So the jump records what it dropped and back spends it. One slot, not a stack: this is the
/// single step Shell cannot take on its own, and a growing history of tab hops is a different and
/// much easier thing to get wrong. Only a jump that actually dropped something records anything —
/// switching tabs from a tab root loses no place, and back there should exit as Android expects.
/// </para>
/// </remarks>
public sealed class NavigationOrigin
{
    private string? _pending;

    /// <summary>Whether a back press has somewhere of the app's own to return to.</summary>
    public bool HasOrigin => _pending is not null;

    /// <summary>
    /// Records the location a tab jump is leaving behind. A later jump overwrites an earlier one:
    /// the slot holds where back goes next, which is always the most recent place left.
    /// </summary>
    public void Remember(string? location) =>
        _pending = string.IsNullOrWhiteSpace(location) ? null : location;

    /// <summary>Forgets any recorded origin — for a jump that deliberately ends the journey.</summary>
    public void Clear() => _pending = null;

    /// <summary>
    /// Takes the recorded origin, if there is one, and forgets it. Taking rather than reading:
    /// one jump earns exactly one return, so a second back press at the same tab root exits
    /// rather than bouncing the caregiver around the app.
    /// </summary>
    public bool TryTake(out string origin)
    {
        origin = _pending ?? string.Empty;
        _pending = null;
        return origin.Length > 0;
    }
}
