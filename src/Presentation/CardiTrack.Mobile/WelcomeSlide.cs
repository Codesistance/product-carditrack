namespace CardiTrack.Mobile;

public sealed class WelcomeSlide
{
    public required ImageSource HeroImage { get; init; }
    public required string Title { get; init; }
    public required string Subtitle { get; init; }

    /// <summary>Top margin below title (Figma y-gap minus title line height).</summary>
    public double SubtitleTopMargin { get; init; } = 16;

    public static IReadOnlyList<WelcomeSlide> DefaultSlides { get; } =
    [
        new WelcomeSlide
        {
            HeroImage = ImageSource.FromFile("welcome_hero_a.png"),
            Title = "Know They're Okay",
            Subtitle = "Stay close to the people you love — even from far away",
            SubtitleTopMargin = 16,
        },
        new WelcomeSlide
        {
            HeroImage = ImageSource.FromFile("welcome_hero_b.png"),
            Title = "Their Watch, Your Peace of Mind",
            Subtitle = "Connects with Fitbit, Apple Watch, Garmin & more",
            SubtitleTopMargin = 42,
        },
        new WelcomeSlide
        {
            HeroImage = ImageSource.FromFile("welcome_hero_c.png"),
            Title = "Care Together",
            Subtitle = "Share the watch with your siblings — you're not in this alone",
            SubtitleTopMargin = 23,
        },
    ];
}
