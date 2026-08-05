namespace CardiTrack.Mobile.Core.Localization;

/// <summary>Example phone-number placeholders per region, using officially reserved fictional ranges.</summary>
public static class PhonePlaceholder
{
    private const string Default = "+1 555 000 0000";

    // US 555 range; UK Ofcom drama range 07700 900000-900999.
    private static readonly Dictionary<string, string> ByRegion = new(StringComparer.OrdinalIgnoreCase)
    {
        ["US"] = Default,
        ["CA"] = Default,
        ["GB"] = "+44 7700 900000",
    };

    /// <summary>Returns the example number for a two-letter ISO region code, falling back to the US format.</summary>
    public static string ForRegion(string? twoLetterIsoRegionName) =>
        twoLetterIsoRegionName is not null && ByRegion.TryGetValue(twoLetterIsoRegionName, out var placeholder)
            ? placeholder
            : Default;
}
