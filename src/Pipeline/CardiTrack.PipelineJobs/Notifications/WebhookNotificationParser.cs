using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace CardiTrack.PipelineJobs.Notifications;

/// <summary>
/// Extracts the health-user ids a notification concerns. The notification body has no schema in
/// the v4 discovery document, so this hunts rather than assumes: it walks the whole JSON and takes
/// an id from either of the two forms a wearer can be named by — a <c>healthUserId</c> property,
/// which is what live traffic carries, or a string rooted at <c>users/{id}</c>. Notify-then-fetch
/// makes both safe — the id only selects which connection to re-sync; no data is read from the
/// payload itself, so an unexpected shape can cost freshness, never correctness.
/// </summary>
/// <remarks>
/// The observed live shape, from the aggregator's own logged description, is a batch of one
/// element per changed data type:
/// <code>
/// [ { "data": { "version": ..., "clientProvidedSubscriptionName": ...,
///               "healthUserId": "...", "operation": ..., "dataType": ...,
///               "intervals": [ ... ] } }, ... ]
/// </code>
/// </remarks>
public static partial class WebhookNotificationParser
{
    /// <summary>
    /// The property live notifications name the wearer with. Matched case-insensitively and
    /// wherever it appears, rather than at a fixed path: the enclosing envelope has already
    /// changed once, and a nesting change must not silently stop the aggregator again.
    /// </summary>
    private const string HealthUserIdProperty = "healthUserId";

    /// <summary>
    /// A user resource name, or any resource rooted at one — <c>users/{id}</c> and
    /// <c>users/{id}/dataTypes/steps</c> alike.
    /// </summary>
    /// <remarks>
    /// Kept as a secondary form, not the primary one. Google names a user this way in the
    /// <c>Subscription</c> resource, and an earlier fix assumed the notification body did the same;
    /// it does not, and reading the shape off live traffic is what settled it. Retained because it
    /// costs nothing, covers the subscription-management shapes, and the cost of guessing wrong
    /// here is silence rather than an error.
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
            switch (token)
            {
                // The form live traffic uses: a named property holding the bare id.
                case JProperty { Value: JValue { Type: JTokenType.String } property } p
                    when string.Equals(p.Name, HealthUserIdProperty, StringComparison.OrdinalIgnoreCase):
                {
                    // Trimmed, because the id is matched against DeviceConnection.HealthUserId
                    // exactly: a padded value would miss, count as an unknown user, and produce
                    // no sync and no error — the same silent shape as the bug this parser is
                    // being fixed for. These ids are opaque alphanumerics, so any surrounding
                    // whitespace is incidental and never part of the identity. Trimming first
                    // also subsumes the empty and whitespace-only cases in one check.
                    if (property.Value<string>()?.Trim() is { Length: > 0 } id)
                        ids.Add(id);
                    break;
                }

                // The resource-name form, wherever a string carries one.
                case JValue { Type: JTokenType.String } value
                    when value.Value<string>() is { } text
                         && UserResourceName().Match(text) is { Success: true } match:
                {
                    ids.Add(match.Groups["id"].Value);
                    break;
                }
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
