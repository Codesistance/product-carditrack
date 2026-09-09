using System.Text.Json;
using System.Text.Json.Nodes;

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
/// The exception is <see cref="Volatile"/>: fields the server regenerates on every response, which
/// say nothing about whether the answer changed. Left in, they make every comparison false and the
/// whole check pointless — a signed photo URL is reissued per request, so two identical member
/// profiles would never compare equal and a screen would redraw under an "Updating…" every time.
/// They are stripped from both sides before comparing.
/// </para>
/// <para>
/// The cost is a serialize per refresh on payloads that are already parsed JSON of a few kilobytes,
/// which is far cheaper than the redundant re-render it avoids — and much cheaper than being
/// wrong. A screen whose freshness genuinely turns on one field can still pass its own comparator;
/// this is the default that is safe not to think about.
/// </para>
/// </remarks>
public static class SamePayload
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Property names, at any depth, that are reissued per response and so are not evidence of a
    /// change. Keep this list short and keep the reason with it: anything here is a field a screen
    /// can never be redrawn for.
    /// </summary>
    /// <remarks>
    /// <c>photoUrl</c> — a signed URL for the member's profile photo, minted per request and good
    /// for minutes (<c>CardiMemberResponse.PhotoUrl</c>). The photo it points at is the same photo;
    /// when a caregiver actually changes one, the mutation evicts the member's cached reads, so the
    /// next load has nothing saved to compare against and draws the new one regardless.
    /// </remarks>
    private static readonly HashSet<string> Volatile = new(StringComparer.OrdinalIgnoreCase)
    {
        "photoUrl",
    };

    /// <summary>Whether <paramref name="a"/> and <paramref name="b"/> are the same answer.</summary>
    public static bool Same<T>(T a, T b)
    {
        if (ReferenceEquals(a, b))
            return true;
        if (a is null || b is null)
            return false;

        try
        {
            return Canonical(a) == Canonical(b);
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

    private static string Canonical<T>(T value)
    {
        var node = JsonSerializer.SerializeToNode(value, Options);
        StripVolatile(node);
        return node?.ToJsonString(Options) ?? "null";
    }

    private static void StripVolatile(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                // Names first, then remove: the object cannot be edited while it is enumerated.
                foreach (var name in o.Where(p => Volatile.Contains(p.Key)).Select(p => p.Key).ToList())
                    o.Remove(name);
                foreach (var property in o)
                    StripVolatile(property.Value);
                break;

            case JsonArray a:
                foreach (var item in a)
                    StripVolatile(item);
                break;
        }
    }
}
