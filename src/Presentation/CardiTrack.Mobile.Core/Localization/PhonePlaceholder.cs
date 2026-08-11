namespace CardiTrack.Mobile.Core.Localization;

/// <summary>
/// Example phone-number placeholders per region, using officially reserved fictional ranges.
/// Digits-only (no spaces) so the example itself is always accepted by the E.164-style validator
/// (<c>^\+?[1-9]\d{1,14}$</c>) both the mobile forms and the API apply — a spaced example would
/// invite a caregiver to type the format they're shown and fail validation on submit.
/// </summary>
public static class PhonePlaceholder
{
    private const string Default = "+15550000000";

    // US 555 range; UK Ofcom drama range 07700 900000-900999.
    private static readonly Dictionary<string, string> ByRegion = new(StringComparer.OrdinalIgnoreCase)
    {
        ["US"] = Default,
        ["CA"] = Default,
        ["GB"] = "+447700900000",
    };

    /// <summary>Returns the example number for a two-letter ISO region code, falling back to the US format.</summary>
    public static string ForRegion(string? twoLetterIsoRegionName) =>
        twoLetterIsoRegionName is not null && ByRegion.TryGetValue(twoLetterIsoRegionName, out var placeholder)
            ? placeholder
            : Default;
}
