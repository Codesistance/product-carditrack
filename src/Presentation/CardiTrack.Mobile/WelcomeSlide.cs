namespace CardiTrack.Mobile;

public sealed class WelcomeSlide
{
    public required ImageSource HeroImage { get; init; }
    public required string Title { get; init; }
    public required string Subtitle { get; init; }

    /// <summary>Top margin below title (Figma subtitle y minus title bottom).</summary>
    public double SubtitleTopMargin { get; init; } = 16;

    /// <summary>Subtitle text-block width from Figma.</summary>
    public double SubtitleMaxWidth { get; init; } = 346;

    public static IReadOnlyList<WelcomeSlide> DefaultSlides { get; } =
    [
        new WelcomeSlide
        {
            HeroImage = ImageSource.FromFile("welcome_hero_a.png"),
            Title = "Know They're Okay",
            Subtitle = "Stay close to the people you love — even from far away",
            SubtitleTopMargin = 9,
            SubtitleMaxWidth = 322,
        },
        new WelcomeSlide
        {
            HeroImage = ImageSource.FromFile("welcome_hero_b.png"),
            Title = "Their Watch, Your Peace",
            Subtitle = "Connects with Fitbit, Apple Watch, Garmin & more",
            SubtitleTopMargin = 16,
            SubtitleMaxWidth = 331,
        },
        new WelcomeSlide
        {
            HeroImage = ImageSource.FromFile("welcome_hero_c.png"),
            Title = "Care Together",
            Subtitle = "Share the watch with your siblings — you're not in this alone",
            SubtitleTopMargin = 16,
            SubtitleMaxWidth = 346,
        },
    ];
}
