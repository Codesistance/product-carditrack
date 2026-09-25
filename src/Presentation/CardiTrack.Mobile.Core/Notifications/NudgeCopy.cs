using System.Text.Json;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Core.Members;

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
        ["nudge.DEVICE_AUTH_BROKEN.expired.title"] = "{name}'s {device} needs reconnecting",
        ["nudge.DEVICE_AUTH_BROKEN.expired.body"] =
            "The connection to {name}'s {device} has expired, so no new data is reaching CardiTrack. Sign in again so readings keep coming through.",
        ["nudge.DEVICE_AUTH_BROKEN.expired.benefit"] = "Reconnecting takes a minute and restores monitoring.",

        ["nudge.DEVICE_AUTH_BROKEN.revoked.title"] = "CardiTrack lost access to {name}'s {device}",
        ["nudge.DEVICE_AUTH_BROKEN.revoked.body"] =
            "Permission to read {name}'s health data was withdrawn, so nothing is being collected.",
        ["nudge.DEVICE_AUTH_BROKEN.revoked.benefit"] = "Reconnect to restore monitoring.",

        // Three severity tiers (BatteryAlertTier), each with a plain reading and a "no percentage
        // reported" variant — a band-only device must not carry a {percent} placeholder nothing
        // will ever substitute.
        ["nudge.DEVICE_BATTERY_LOW.warning.title"] = "{name}'s watch battery is getting low",
        ["nudge.DEVICE_BATTERY_LOW.warning.body"] =
            "It's down to {percent}%. Charging it tonight avoids a gap in monitoring.",
        ["nudge.DEVICE_BATTERY_LOW.warning.benefit"] = "Charging it keeps monitoring running smoothly.",

        ["nudge.DEVICE_BATTERY_LOW.urgent.title"] = "{name}'s watch battery is running low",
        ["nudge.DEVICE_BATTERY_LOW.urgent.body"] =
            "It's down to {percent}%, and could stop collecting within a few hours.",
        ["nudge.DEVICE_BATTERY_LOW.urgent.benefit"] = "Charging it now avoids a gap in monitoring.",

        // Band-only devices report "Low" with no percentage — the same tier as a known 11-20%
        // reading, but the sentence has no number to give.
        ["nudge.DEVICE_BATTERY_LOW.urgent_unknown.title"] = "{name}'s watch battery is running low",
        ["nudge.DEVICE_BATTERY_LOW.urgent_unknown.body"] = "It could stop collecting soon if it isn't charged.",
        ["nudge.DEVICE_BATTERY_LOW.urgent_unknown.benefit"] = "Charging it now avoids a gap in monitoring.",

        ["nudge.DEVICE_BATTERY_LOW.critical.title"] = "{name}'s watch is almost out of battery",
        ["nudge.DEVICE_BATTERY_LOW.critical.body"] =
            "It's down to {percent}%, and will stop collecting when it runs out.",
        ["nudge.DEVICE_BATTERY_LOW.critical.benefit"] = "Charging it now keeps monitoring unbroken.",

        // A device reporting "Empty" has already stopped, whatever its last percentage said — a
        // materially different sentence from "at 8%", not just the same one with the number gone.
        ["nudge.DEVICE_BATTERY_LOW.critical_empty.title"] = "{name}'s watch has run out of battery",
        ["nudge.DEVICE_BATTERY_LOW.critical_empty.body"] =
            "It's stopped collecting, so nothing new is reaching CardiTrack.",
        ["nudge.DEVICE_BATTERY_LOW.critical_empty.benefit"] = "Charging it restores monitoring.",

        // ---- Blocking: core value is unavailable ----
        ["nudge.DEVICE_REMOVED.title"] = "{name} has no connected wearable",
        ["nudge.DEVICE_REMOVED.body"] = "Nothing is being collected for {name} at the moment.",
        ["nudge.DEVICE_REMOVED.benefit"] =
            "Connect one to start seeing daily activity, heart rate and sleep again.",

        ["nudge.DEVICE_STALE_LONG.title"] = "{name}'s watch hasn't synced in two days",
        ["nudge.DEVICE_STALE_LONG.body"] = "The last reading arrived {hours} hours ago.",
        ["nudge.DEVICE_STALE_LONG.benefit"] =
            "A charge, or opening the watch's own app once, usually gets it flowing again.",

        // A connection can sit at Connected with no LastSyncDate at all — never having synced once
        // is a different fact than an "{hours} ago" gap, so it carries no {hours} placeholder.
        ["nudge.DEVICE_STALE_LONG.never_synced.title"] = "{name}'s watch hasn't synced yet",
        ["nudge.DEVICE_STALE_LONG.never_synced.body"] = "No reading has arrived from this watch yet.",
        ["nudge.DEVICE_STALE_LONG.never_synced.benefit"] =
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

        // Says what is not happening, never what might be wrong. The subject is the watch's
        // settings, not the wearer's heart, and a caregiver who reads this as "something has been
        // found" has been frightened for nothing. "Checking for" rather than "screening for": the
        // second sounds like a test someone has already had.
        ["nudge.IRN_NOT_ENROLLED.title"] = "{name}'s watch isn't checking heart rhythm",
        ["nudge.IRN_NOT_ENROLLED.body"] =
            "Their watch can check for an irregular heart rhythm in the background, but that is "
            + "switched off.",
        ["nudge.IRN_NOT_ENROLLED.benefit"] =
            "With it on, their watch tells {name} if it notices something, and CardiTrack passes "
            + "that on to you. It is a setting on their watch, so you may need to be with them.",

        ["nudge.EMERGENCY_CONTACT_MISSING.title"] = "Add an emergency contact for {name}",
        ["nudge.EMERGENCY_CONTACT_MISSING.body"] = "No number is saved, so SOS and Call have nowhere to go.",
        ["nudge.EMERGENCY_CONTACT_MISSING.benefit"] =
            "One number turns both into a single tap, for you and for anyone else watching over "
            + "{name}.",

        ["nudge.MEDICAL_NOTES_EMPTY.title"] = "Add {name}'s health background",
        ["nudge.MEDICAL_NOTES_EMPTY.body"] = "No conditions or medications are recorded.",
        ["nudge.MEDICAL_NOTES_EMPTY.benefit"] =
            "Health insights and the doctor-visit report get far more specific with them. "
            + "Encrypted, and visible only to your family.",

        // The only rule that asks about the health background a second time. Its two variants
        // are the two things we can honestly say: how long since somebody confirmed it, and — for
        // notes that predate the review date — that nobody ever has. No figure in the second,
        // because the one we could compute is inferred from a join date and would be stating
        // something we cannot support.
        ["nudge.MEDICAL_NOTES_STALE.title"] = "Is {name}'s health background still right?",
        ["nudge.MEDICAL_NOTES_STALE.body"] =
            "It's been about {months} months since anyone confirmed it.",
        ["nudge.MEDICAL_NOTES_STALE.benefit"] =
            "Conditions and medications change. Health insights and the doctor-visit report read "
            + "these notes as current, so a quick check keeps them worth reading.",

        ["nudge.MEDICAL_NOTES_STALE.never_confirmed.title"] = "Is {name}'s health background still right?",
        ["nudge.MEDICAL_NOTES_STALE.never_confirmed.body"] =
            "These notes have been on file a while and nobody has confirmed them.",
        ["nudge.MEDICAL_NOTES_STALE.never_confirmed.benefit"] =
            "Conditions and medications change. Health insights and the doctor-visit report read "
            + "these notes as current, so a quick check keeps them worth reading.",

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
    /// Fills <c>{name}</c> with the member's first name — resolved separately, see remarks — and every other
    /// placeholder from the stored template data.
    /// </summary>
    /// <remarks>
    /// The name arrives on the response rather than inside <see cref="NotificationResponse.TemplateData"/>
    /// by design — the stored row holds counters only, so a wearer's name never sits in the same
    /// record as the health-derived gap describing them.
    /// </remarks>
    private static string Substitute(string template, NotificationResponse n)
    {
        var result = template.Replace("{name}", n.MemberFirstName() ?? "your family member",
            StringComparison.Ordinal);

        if (!result.Contains('{') || string.IsNullOrWhiteSpace(n.TemplateData))
            return WithoutMissingDevice(result);

        foreach (var (token, value) in ParseTemplateData(n.TemplateData))
            result = result.Replace($"{{{token}}}", value, StringComparison.Ordinal);

        return WithoutMissingDevice(result);
    }

    /// <summary>
    /// "{device}" reached the reconnect reminder's data after its copy did: a reminder raised
    /// before then has none, and reads "watch" rather than showing the placeholder.
    /// </summary>
    private static string WithoutMissingDevice(string text) =>
        text.Replace("{device}", "watch", StringComparison.Ordinal);

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
