using System.Text.Json;

namespace CardiTrack.Mobile.Core.Offline;

/// <summary>
/// "Is the live answer the saved one over again?" — the test <see cref="SnapshotRefresh"/> uses to
/// decide whether a screen needs redrawing at all.
/// </summary>
/// <remarks>
/// <para>
/// Compared as whole payloads rather than field by field, deliberately. A hand-written comparator
/// has to name every field the screen draws from, and it is wrong the moment either side grows one
/// — the response gains a field, or the page starts rendering one it already had. The failure is
/// silent and it is the bad direction: the run concludes "nothing changed", skips the render, and
/// leaves the caregiver reading the old answer. That is exactly what happened here — comparators
/// written against an id and an on/off flag would have held a rule at "Soon" after it went live,
/// and held an alarm's condition, severity and "waiting for data" pill after they moved.
/// </para>
/// <para>
/// So the comparison is the serialized payload, which cannot drift from what the server sent. The
/// cost is a serialize per refresh on payloads that are already parsed JSON of a few kilobytes,
/// which is far cheaper than the redundant re-render it avoids — and much cheaper than being
/// wrong. A screen whose freshness genuinely turns on one field (a generation timestamp, say) can
/// still pass its own comparator; this is the default that is safe not to think about.
/// </para>
/// </remarks>
public static class SamePayload
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>Whether <paramref name="a"/> and <paramref name="b"/> serialize identically.</summary>
    public static bool Same<T>(T a, T b)
    {
        if (ReferenceEquals(a, b))
            return true;
        if (a is null || b is null)
            return false;

        try
        {
            return JsonSerializer.Serialize(a, Options) == JsonSerializer.Serialize(b, Options);
        }
        catch (Exception ex) when (ex is NotSupportedException or JsonException or InvalidOperationException)
        {
            // A payload this cannot serialize is one we cannot call unchanged. Redrawing is
            // always safe; claiming equality is not — and throwing is worst of all, since this
            // runs inside a screen's load and would take the page down over a comparison that
            // only ever decides whether to skip work. Converter faults, cycles and max-depth all
            // land here.
            return false;
        }
    }
}
