namespace CardiTrack.Mobile.Services;

/// <summary>
/// The four-tier data-pipeline freshness colour, in one place. The API sends the tier as a
/// word; every screen that draws it — the dashboard hero, the CardiMember card, the sync
/// popup behind that card's dot — reads the same mapping, so a tier cannot come out amber on
/// one screen and blue on the next.
/// </summary>
public static class FreshnessPalette
{
    public static string ColorKeyFor(string? tier) => tier switch
    {
        "red" => "StatusRed",
        "amber" => "StatusYellow",
        "blue" => "StatusBlue",
        "green" => "StatusGreen",
        _ => "StatusUnknown",
    };

    /// <summary>The tier's colour from the app resources.</summary>
    public static Color ColorFor(string? tier) =>
        (Color)Microsoft.Maui.Controls.Application.Current!.Resources[ColorKeyFor(tier)];
}
