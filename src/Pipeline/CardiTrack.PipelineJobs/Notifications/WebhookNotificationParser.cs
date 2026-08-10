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
    /// The body's top-level property names — safe to log (names, never values) and exactly what
    /// pins the real notification schema on first live traffic.
    /// </summary>
    public static string TopLevelShape(string body)
    {
        try
        {
            return JToken.Parse(body) is JObject obj
                ? string.Join(",", obj.Properties().Select(p => p.Name))
                : "(non-object)";
        }
        catch (Newtonsoft.Json.JsonReaderException)
        {
            return "(not json)";
        }
    }
}
