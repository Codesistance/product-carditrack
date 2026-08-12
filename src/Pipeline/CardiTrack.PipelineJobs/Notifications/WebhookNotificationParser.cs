using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace CardiTrack.PipelineJobs.Notifications;

/// <summary>
/// Extracts the health-user ids a notification concerns. The notification body has no schema in
/// the v4 discovery document, so this does not assume field names: it walks the whole JSON and
/// collects every string shaped like a user resource name (`users/{id}`). Notify-then-fetch
/// makes this safe — the id only selects which connection to re-sync; no data is read from the
/// payload itself, so an unexpected shape can cost freshness, never correctness.
/// </summary>
public static partial class WebhookNotificationParser
{
    [GeneratedRegex(@"^users/(?<id>[A-Za-z0-9._\-]+)$")]
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
    /// The body's shape — safe to log (property names and JSON types, never values) and exactly
    /// what pins the real notification schema on first live traffic. An object yields its
    /// top-level property names; an array yields its length plus the distinct shapes of its
    /// elements (each element's property names if it's an object, its JSON type otherwise), since
    /// request sizes observed from live Google Health traffic vary in a way a single fixed object
    /// does not explain — a batched array is the leading suspect.
    /// </summary>
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

        return root switch
        {
            JObject obj => string.Join(",", obj.Properties().Select(p => p.Name)),
            JArray array => DescribeArray(array),
            _ => "(non-object)"
        };
    }

    private static string DescribeArray(JArray array)
    {
        if (array.Count == 0)
            return "array[0]";

        // Property names are sorted within an element (JSON key order carries no meaning and
        // isn't guaranteed stable across elements from the same producer) and the distinct
        // shapes are sorted too, so equivalent shapes collapse to one entry instead of
        // inflating the list with order-only variants.
        var elementShapes = array
            .Select(element => element is JObject obj
                ? string.Join("+", obj.Properties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal))
                : element.Type.ToString())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(shape => shape, StringComparer.Ordinal);

        return $"array[{array.Count}]:{string.Join("|", elementShapes)}";
    }
}
