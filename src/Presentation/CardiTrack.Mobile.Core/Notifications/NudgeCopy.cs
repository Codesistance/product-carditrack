using System.Text.Json;
using CardiTrack.Application.DTOs.Responses;

namespace CardiTrack.Mobile.Core.Notifications;

/// <summary>
/// Resolves the localization keys a notification carries into the words a caregiver reads.
/// </summary>
/// <remarks>
/// <para>
/// The server stores keys, not sentences, so copy can be rewritten or translated in a client
/// release rather than a data migration — and so a notification raised last month renders in
/// today's wording.
/// </para>
/// <para>
/// House rules for anything added here: <b>benefit first, guilt never.</b> Name the capability the
/// missing information unlocks, never what the user failed to do. And never promise something that
/// is not built — a prompt trading a caregiver's effort for a feature we do not ship spends the
/// trust the whole surface runs on.
/// </para>
/// </remarks>
public static class NudgeCopy
{
    private const string Fallback = "CardiTrack needs a little more information.";

    private static readonly Dictionary<string, string> Strings = new(StringComparer.Ordinal)
    {
        // ---- Safety: monitoring is degraded ----
        ["nudge.DEVICE_AUTH_BROKEN.expired.title"] = "{name}'s watch needs reconnecting",
        ["nudge.DEVICE_AUTH_BROKEN.expired.body"] =
            "The connection to {name}'s watch has expired, so no new data is reaching CardiTrack.",
        ["nudge.DEVICE_AUTH_BROKEN.expired.benefit"] = "Reconnecting takes a minute and restores monitoring.",

        ["nudge.DEVICE_AUTH_BROKEN.revoked.title"] = "CardiTrack lost access to {name}'s watch",
        ["nudge.DEVICE_AUTH_BROKEN.revoked.body"] =
            "Permission to read {name}'s health data was withdrawn, so nothing is being collected.",
        ["nudge.DEVICE_AUTH_BROKEN.revoked.benefit"] = "Reconnect to restore monitoring.",

        // ---- Blocking: core value is unavailable ----
        ["nudge.DEVICE_REMOVED.title"] = "{name} has no connected wearable",
        ["nudge.DEVICE_REMOVED.body"] = "Nothing is being collected for {name} at the moment.",
        ["nudge.DEVICE_REMOVED.benefit"] =
            "Connect one to start seeing daily activity, heart rate and sleep again.",

        ["nudge.DEVICE_STALE_LONG.title"] = "{name}'s watch hasn't synced in two days",
        ["nudge.DEVICE_STALE_LONG.body"] = "The last reading arrived {hours} hours ago.",
        ["nudge.DEVICE_STALE_LONG.benefit"] =
            "A charge, or opening the watch's own app once, usually gets it flowing again.",

        ["nudge.TIMEZONE_DEFAULT.title"] = "Set your time zone",
        ["nudge.TIMEZONE_DEFAULT.body"] = "Your account is still on UTC.",
        ["nudge.TIMEZONE_DEFAULT.benefit"] =
            "Setting it means \"no activity yet today\" and daily summaries use your clock, not UTC.",

        ["nudge.BASELINE_STALLED.title"] = "{name}'s learning has stalled",
        ["nudge.BASELINE_STALLED.body"] = "{captured} of {required} days captured, and nothing new recently.",
        ["nudge.BASELINE_STALLED.benefit"] =
            "CardiTrack needs a full picture of {name}'s normal week before it can spot a change.",

        // ---- Unlock: supply this, get that ----
        ["nudge.SLEEP_SCOPE_MISSING.title"] = "Sleep isn't shared yet",
        ["nudge.SLEEP_SCOPE_MISSING.body"] = "{name}'s watch is connected, but sleep data isn't included.",
        ["nudge.SLEEP_SCOPE_MISSING.benefit"] =
            "Granting sleep access lets CardiTrack track {name}'s sleep patterns and nightly trends.",

        ["nudge.MEDICAL_NOTES_EMPTY.title"] = "Add {name}'s health background",
        ["nudge.MEDICAL_NOTES_EMPTY.body"] = "No conditions or medications are recorded.",
        ["nudge.MEDICAL_NOTES_EMPTY.benefit"] =
            "Health insights and the doctor-visit report get far more specific with them. "
            + "Encrypted, and visible only to your family.",

        // ---- Account lifecycle ----
        ["nudge.PAUSE_LEFT_LONG.title"] = "{name} is paused for another {days} days",
        ["nudge.PAUSE_LEFT_LONG.body"] = "No monitoring or alerts while the pause runs.",
        ["nudge.PAUSE_LEFT_LONG.benefit"] = "Resume when you're ready, or leave it if that's deliberate."
    };

    public static string Title(NotificationResponse n) => Resolve(n.TitleKey, n);
    public static string Body(NotificationResponse n) => Resolve(n.BodyKey, n);
    public static string Benefit(NotificationResponse n) => Resolve(n.BenefitKey, n);

    /// <summary>
    /// A one-line summary for the compact dashboard card, where only the headline fits.
    /// </summary>
    public static string Headline(NotificationResponse n) => Title(n);

    private static string Resolve(string? key, NotificationResponse n)
    {
        if (string.IsNullOrWhiteSpace(key) || !Strings.TryGetValue(key, out var template))
            return Fallback;

        return Substitute(template, n);
    }

    /// <summary>
    /// Fills <c>{name}</c> from the response's separately-resolved member name, and every other
    /// placeholder from the stored template data.
    /// </summary>
    /// <remarks>
    /// The name arrives on the response rather than inside <see cref="NotificationResponse.TemplateData"/>
    /// by design — the stored row holds counters only, so a wearer's name never sits in the same
    /// record as the health-derived gap describing them.
    /// </remarks>
    private static string Substitute(string template, NotificationResponse n)
    {
        var result = template.Replace("{name}", n.CardiMemberName ?? "your family member",
            StringComparison.Ordinal);

        if (!result.Contains('{') || string.IsNullOrWhiteSpace(n.TemplateData))
            return result;

        foreach (var (token, value) in ParseTemplateData(n.TemplateData))
            result = result.Replace($"{{{token}}}", value, StringComparison.Ordinal);

        return result;
    }

    private static IEnumerable<KeyValuePair<string, string>> ParseTemplateData(string json)
    {
        Dictionary<string, JsonElement>? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
        }
        catch (JsonException)
        {
            // Unreadable substitutions should cost the sentence its numbers, not the whole card.
            yield break;
        }

        if (parsed is null)
            yield break;

        foreach (var (key, element) in parsed)
        {
            yield return new(key, element.ValueKind switch
            {
                JsonValueKind.String => element.GetString() ?? string.Empty,
                JsonValueKind.Number => element.ToString(),
                _ => element.ToString()
            });
        }
    }
}
