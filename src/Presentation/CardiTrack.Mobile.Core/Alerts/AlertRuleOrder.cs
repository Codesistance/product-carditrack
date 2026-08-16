using CardiTrack.Application.DTOs.Responses;

namespace CardiTrack.Mobile.Core.Alerts;

/// <summary>
/// The order the alert rules of one cluster are read in on Member Detail.
/// </summary>
/// <remarks>
/// Availability first, then the title. Availability leads because this is a list of switches: the
/// rules a caregiver can actually turn on or off are what they came here for, and the reserved
/// "Soon" ids are a roadmap they are only browsing. The title breaks the tie because the previous
/// tie-break was the catalogue's own declaration order — an editorial sequence that reads fine
/// once and badly on the tenth visit, when the caregiver is looking for one named rule and has no
/// way to guess where it sits.
/// </remarks>
public static class AlertRuleOrder
{
    /// <summary>
    /// Available rules first, each group alphabetical by title. Ordinal-ignore-case rather than
    /// culture: the order must not shift under a caregiver whose phone changes locale, and these
    /// titles are the same English strings for everyone until the app is localised.
    /// </summary>
    public static IReadOnlyList<AlertRuleSettingResponse> ForDisplay(
        IEnumerable<AlertRuleSettingResponse>? rules) =>
        rules is null
            ? []
            : rules
                .OrderBy(r => r.IsImplemented ? 0 : 1)
                .ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
}
