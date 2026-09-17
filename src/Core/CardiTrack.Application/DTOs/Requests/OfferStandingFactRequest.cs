namespace CardiTrack.Application.DTOs.Requests;

/// <summary>
/// A standing fact the family volunteered, rather than an answer to a question the service asked.
/// </summary>
public class OfferStandingFactRequest
{
    /// <summary>
    /// What they want us to keep. Free text, same cap as an answer — this is the same store.
    /// </summary>
    public string FactText { get; set; } = string.Empty;
}
