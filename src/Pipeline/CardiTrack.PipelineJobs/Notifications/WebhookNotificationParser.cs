using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace CardiTrack.PipelineJobs.Notifications;

/// <summary>
/// Extracts the health-user ids a notification concerns. The notification body has no schema in
/// the v4 discovery document, so this does not assume field names: it walks the whole JSON and
/// collects the id out of every string shaped like a user resource name — `users/{id}` itself,
/// or anything rooted at it such as `users/{id}/dataTypes/steps`. Notify-then-fetch makes this
/// safe — the id only selects which connection to re-sync; no data is read from the payload
/// itself, so an unexpected shape can cost freshness, never correctness.
/// </summary>
public static partial class WebhookNotificationParser
{
    /// <summary>
    /// A user resource name, or any resource rooted at one. The trailing path is deliberately
    /// tolerated rather than rejected.
    /// </summary>
    /// <remarks>
    /// This originally anchored at `$`, accepting a bare `users/{id}` only, on the reasoning that
    /// `users/{id}/dataTypes/steps` names a collection *under* a user rather than the user. True
    /// as resource naming, and wrong for this job: every live notification was rejected by it.
    /// The aggregator ran for days reporting `unparseable` on every message and syncing nothing,
    /// which looked like health because the routine poll silently covered for it.
    /// <para>
    /// Google's own <c>Subscription.dataTypes</c> is documented in the v4 discovery document as
    /// <c>"users/{health_user_id}/dataTypes/{data_type}"</c>, and the observed payloads are
    /// batches of 4–6 elements — one per changed data type, each naming its user that way. The
    /// only question this parser exists to answer is *which wearer changed*, and that longer name
    /// answers it exactly as definitively as the short one.
    /// </para>
    /// </remarks>
    [GeneratedRegex(@"^users/(?<id>[A-Za-z0-9._\-]+)(?:/.*)?$")]
    private static partial Regex UserResourceName();

    /// <summary>Distinct health-user ids found in the body; empty when none, or when the body is
    /// not JSON at all.</summary>
    public static IReadOnlyCollection<string> ExtractHealthUserIds(string body)
    {
        JToken root;
        try
        {
            root = JToken.Parse(body);
        }
        catch (Newtonsoft.Json.JsonReaderException)
        {
            return [];
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        IEnumerable<JToken> tokens = root is JContainer container
            ? container.DescendantsAndSelf()
            : [root];
        foreach (var token in tokens)
        {
            if (token is JValue { Type: JTokenType.String } value
                && value.Value<string>() is { } text
                && UserResourceName().Match(text) is { Success: true } match)
            {
                ids.Add(match.Groups["id"].Value);
            }
        }

        return ids;
    }

    /// <summary>
    /// How deep <see cref="TopLevelShape"/> descends. Three levels reaches the leaf of an
    /// `array → element → property → value` payload, which is the shape live traffic actually
    /// takes; deeper increases the chance of producing a very long log line.
    /// </summary>
    private const int MaxShapeDepth = 3;

    /// <summary>
    /// The body's shape — safe to log (property names and JSON types, never values) and exactly
    /// what pins the real notification schema on live traffic. An object yields its property
    /// names; an array yields its length plus the distinct shapes of its elements.
    /// </summary>
    /// <remarks>
    /// Descends <see cref="MaxShapeDepth"/> levels rather than one. The single-level version
    /// reported <c>array[4]:data</c> for every live notification — enough to confirm batching,
    /// and not enough to say what was inside <c>data</c>, so diagnosing the parse failure needed
    /// a second deploy to see one level further. Values are still never emitted: a string is
    /// reported as <c>String</c>, never its content, so a payload carrying a pseudonymous user id
    /// remains as safe to log as it was before.
    /// </remarks>
    public static string TopLevelShape(string body)
    {
        JToken root;
        try
        {
            root = JToken.Parse(body);
        }
        catch (Newtonsoft.Json.JsonReaderException)
        {
            return "(not json)";
        }

        return Describe(root, MaxShapeDepth);
    }

    private static string Describe(JToken token, int depth) => token switch
    {
        JObject obj => DescribeObject(obj, depth),
        JArray array => DescribeArray(array, depth),
        _ => token.Type.ToString()
    };

    private static string DescribeObject(JObject obj, int depth)
    {
        if (obj.Count == 0)
            return "{}";

        // Out of depth: name the keys but stop describing what hangs off them.
        if (depth <= 1)
            return string.Join(",", obj.Properties().Select(p => p.Name));

        return string.Join(",", obj.Properties()
            .Select(p => p.Value is JObject or JArray
                ? $"{p.Name}:{{{Describe(p.Value, depth - 1)}}}"
                : $"{p.Name}:{p.Value.Type}"));
    }

    private static string DescribeArray(JArray array, int depth)
    {
        if (array.Count == 0)
            return "array[0]";

        // Property names are sorted within an element (JSON key order carries no meaning and
        // isn't guaranteed stable across elements from the same producer) and the distinct
        // shapes are sorted too, so equivalent shapes collapse to one entry instead of
        // inflating the list with order-only variants.
        var elementShapes = array
            .Select(element => element is JObject obj
                ? string.Join("+", obj.Properties()
                    .OrderBy(p => p.Name, StringComparer.Ordinal)
                    .Select(p => depth <= 1
                        ? p.Name
                        : p.Value is JObject or JArray
                            ? $"{p.Name}:{{{Describe(p.Value, depth - 1)}}}"
                            : $"{p.Name}:{p.Value.Type}"))
                : element.Type.ToString())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(shape => shape, StringComparer.Ordinal);

        return $"array[{array.Count}]:{string.Join("|", elementShapes)}";
    }
}
