namespace CardiTrack.Mobile.Services;

/// <summary>
/// Maps a weather provider's free-text condition to a single display glyph. The condition is
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

    public static string For(string? condition)
    {
        if (string.IsNullOrWhiteSpace(condition))
            return Fallback;

        foreach (var (keyword, glyph) in Keywords)
        {
            if (condition.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return glyph;
        }

        return Fallback;
    }
}
