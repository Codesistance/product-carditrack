namespace CardiTrack.Application.DTOs.Responses;

public class AdviseResponse
{
    public required Guid CardiMemberId { get; init; }
    public required string Summary { get; init; }
    public required string Suggestion { get; init; }

    /// <summary>
    /// Which wellness reference the suggestion drew on, in the model's own words. Null when the
    /// model found nothing in the readings that fit any of the references — clients should treat a
    /// null the same as an empty <see cref="Suggestion"/>, not render a suggestion without its
    /// grounding.
    /// </summary>
    public string? GuidelineCited { get; init; }

    public required DateTimeOffset GeneratedAt { get; init; }
}
