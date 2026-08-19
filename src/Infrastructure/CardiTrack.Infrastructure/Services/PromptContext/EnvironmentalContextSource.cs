using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Entities;

namespace CardiTrack.Infrastructure.Services.PromptContext;

/// <summary>
/// The weather the member last exercised in — temperature, air quality, conditions and humidity,
/// derived from a GPS-tagged session and never from a stored coordinate.
/// </summary>
/// <remarks>
/// <para>
/// This reading existed before the framework did, and reached exactly one prompt: the assessor,
/// because that is the service the enrichment pass was built alongside. It belongs in the others
/// too — a hot, humid afternoon explains a quieter day in a family summary as readily as it
/// explains an elevated heart rate in an assessment. Moving it here is what makes that true
/// without three copies of the formatting.
/// </para>
/// <para>
/// Consent is checked before the query, not after: only a consented member can ever have a row, so
/// the common case across the whole fleet skips the roundtrip rather than making it and finding
/// nothing.
/// </para>
/// </remarks>
internal sealed class EnvironmentalContextSource : IMemberContextSource
{
    /// <summary>
    /// How old a session's conditions may be before they stop being context for this prompt.
    /// </summary>
    /// <remarks>
    /// Per purpose, because the prompts are asking different questions. The assessor reads one hour
    /// and must not attribute this hour's heart rate to yesterday's heat, so it keeps the three
    /// hours it used before this source existed. A digest describes a whole day, an alert insight
    /// explains something that happened during one, and a trend analysis reaches back further
    /// still — each can honestly mention a session further back than the one before it.
    /// </remarks>
    private static TimeSpan MaxAgeFor(PromptPurpose purpose) => purpose switch
    {
        PromptPurpose.RealtimeAssessment => TimeSpan.FromHours(3),
        PromptPurpose.CurrentStatus => TimeSpan.FromHours(6),
        PromptPurpose.BaselineInsight => TimeSpan.FromHours(48),
        _ => TimeSpan.FromHours(24),
    };

    private readonly IUnitOfWork _unitOfWork;

    public EnvironmentalContextSource(IUnitOfWork unitOfWork) => _unitOfWork = unitOfWork;

    // Everything except the journal's books: this source renders the latest session against
    // UtcNow, which for an account of a finished period is the wrong session on the wrong clock.
    // The daybook fetches the reviewed day's own sessions instead
    // (DaybookPrompt.ConditionsSection); the weekbook asks nothing of the weather at all, and a
    // single session from today would be context its brief never mentions and cannot place.
    public PromptPurpose Purposes =>
        PromptPurpose.All & ~PromptPurpose.Daybook & ~PromptPurpose.Weekbook & ~PromptPurpose.Monthbook;

    public int Order => 10;

    public async Task<MemberContextSection?> BuildAsync(MemberContextRequest request, CancellationToken ct)
    {
        if (request.Member?.EnvironmentalContextConsentGranted != true)
            return null;

        var reading = await _unitOfWork.EnvironmentalReadings.GetLatestAsync(request.CardiMemberId, ct);
        if (reading is null)
            return null;

        var age = request.UtcNow - reading.SessionEndUtc;
        if (age > MaxAgeFor(request.Purpose))
            return null;

        var body = Describe(reading, age);
        return body is null ? null : new MemberContextSection(SectionLabel(request.Purpose), body);
    }

    /// <summary>
    /// What to call this section, which is not the same thing to every prompt.
    /// </summary>
    /// <remarks>
    /// The assessor reads one hour and needs to know these conditions belong to a session, because
    /// attributing this hour's heart rate to a session that ended two hours ago is exactly the
    /// mistake it must not make. A digest describes a whole day and reads the same row as the
    /// weather the person has been out in — labelling that "during a recent exercise session" invited
    /// the model to treat it as a detail of the exercise rather than as the conditions the day's
    /// readings happened in, which is the one thing it is there to be.
    /// </remarks>
    private static string SectionLabel(PromptPurpose purpose) => purpose switch
    {
        PromptPurpose.RealtimeAssessment => "Conditions during a recent exercise session",
        _ => "Conditions they have recently been out in",
    };

    /// <summary>
    /// One line, or null when the enrichment pass found nothing at all. Every field is independently
    /// optional — the lookup can succeed for weather and fail for air quality — so the line is built
    /// from whatever came back rather than gated on all of it.
    /// </summary>
    private static string? Describe(EnvironmentalReading reading, TimeSpan age)
    {
        var parts = new List<string>();

        if (reading.TemperatureCelsius is { } temperature)
            parts.Add($"{temperature:F0}°C");
        if (reading.WeatherCondition is { } condition && !string.IsNullOrWhiteSpace(condition))
            parts.Add(MedicalPromptBlocks.Flatten(condition));
        if (reading.RelativeHumidityPercent is { } humidity)
            parts.Add($"humidity {humidity}%");
        if (reading.AirQualityCategory is { } category && !string.IsNullOrWhiteSpace(category))
            parts.Add($"air quality {MedicalPromptBlocks.Flatten(category)}");

        if (parts.Count == 0)
            return null;

        // How long ago matters as much as what it was: the same reading is context for an hour ago
        // and trivia for yesterday, and only the model can weigh that if it is told which it is.
        var hours = (int)Math.Round(age.TotalHours, MidpointRounding.AwayFromZero);
        var when = hours <= 0 ? "session ended within the hour" : $"session ended about {hours}h ago";

        return $"{string.Join(", ", parts)} ({when})";
    }
}
