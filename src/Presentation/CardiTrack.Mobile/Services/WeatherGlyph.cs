namespace CardiTrack.Mobile.Services;

/// <summary>
/// Maps a weather provider's free-text condition to a single display glyph — an emoji for the
/// weather chips, a drawn icon for the weather popup (<see cref="IconFor"/>). The condition is
/// prose from the provider ("Light rain", "Partly cloudy") with no fixed vocabulary — see
/// <c>EnvironmentalReading.WeatherCondition</c> — so this reads it by keyword rather than by
/// exact match, the same stance the AI prompt context takes toward the same field.
/// </summary>
public static class WeatherGlyph
{
    /// <summary>Checked in order — "partly cloudy" must be caught before the bare "cloud" match
    /// further down, and a thunderstorm read before the plain "rain" it also contains.</summary>
    private static readonly (string Keyword, string Glyph)[] Keywords =
    [
        ("thunder", "⛈️"),
        ("storm", "⛈️"),
        ("snow", "❄️"),
        ("sleet", "🌨️"),
        ("hail", "🌨️"),
        ("drizzle", "🌦️"),
        ("rain", "🌧️"),
        ("shower", "🌧️"),
        ("fog", "🌫️"),
        ("mist", "🌫️"),
        ("haze", "🌫️"),
        ("partly cloudy", "⛅"),
        ("partly sunny", "⛅"),
        ("overcast", "☁️"),
        ("cloud", "☁️"),
        ("wind", "💨"),
        ("clear", "☀️"),
        ("sunny", "☀️"),
    ];

    /// <summary>A plain thermometer for a condition the app has no glyph for, or none at all —
    /// the temperature beside it still says something even when the sky description doesn't.</summary>
    private const string Fallback = "🌡️";

    /// <summary>
    /// The same conditions as the app's own drawn glyphs (<c>icon_weather_*</c>), for the weather
    /// popup's badge, where an emoji drew in each platform's own colours beside a card drawn in
    /// the app's. Keyed by the keyword that matched, so the emoji and the icon come from one
    /// ordered match and cannot disagree about what the sky is. Six drawings for eighteen words:
    /// the icon set is coarser than the emoji one, and wind has no drawing of its own.
    /// </summary>
    private static readonly Dictionary<string, string> Icons = new(StringComparer.Ordinal)
    {
        ["thunder"] = "icon_weather_storm.svg",
        ["storm"] = "icon_weather_storm.svg",
        ["snow"] = "icon_weather_snow.svg",
        ["sleet"] = "icon_weather_snow.svg",
        ["hail"] = "icon_weather_snow.svg",
        ["drizzle"] = "icon_weather_rain.svg",
        ["rain"] = "icon_weather_rain.svg",
        ["shower"] = "icon_weather_rain.svg",
        ["fog"] = "icon_weather_fog.svg",
        ["mist"] = "icon_weather_fog.svg",
        ["haze"] = "icon_weather_fog.svg",
        ["partly cloudy"] = "icon_weather_cloud.svg",
        ["partly sunny"] = "icon_weather_cloud.svg",
        ["overcast"] = "icon_weather_cloud.svg",
        ["cloud"] = "icon_weather_cloud.svg",
        ["clear"] = "icon_weather_sun.svg",
        ["sunny"] = "icon_weather_sun.svg",
    };

    /// <summary>The thermometer again, drawn — for no condition, one without a drawing, or wind.</summary>
    private const string FallbackIcon = "icon_weather_temperature.svg";

    public static string For(string? condition) =>
        Match(condition) is { } match ? match.Glyph : Fallback;

    /// <summary>The drawn glyph for <paramref name="condition"/>: an <c>icon_weather_*</c> file name.</summary>
    public static string IconFor(string? condition) =>
        Match(condition) is { } match && Icons.TryGetValue(match.Keyword, out var icon) ? icon : FallbackIcon;

    private static (string Keyword, string Glyph)? Match(string? condition)
    {
        if (string.IsNullOrWhiteSpace(condition))
            return null;

        foreach (var entry in Keywords)
        {
            if (condition.Contains(entry.Keyword, StringComparison.OrdinalIgnoreCase))
                return entry;
        }

        return null;
    }
}
