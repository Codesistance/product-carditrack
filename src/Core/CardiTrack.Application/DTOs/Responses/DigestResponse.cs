namespace CardiTrack.Application.DTOs.Responses;

/// <summary>
/// One generated summary as the apps read it. <c>LocalDate</c> is the member's local calendar day
/// the text describes — already local, so clients render it without timezone arithmetic.
/// <c>GeneratedAtUtc</c> is when this particular generation ran: summaries are recomputed as the
/// member's data moves, so it is not a property of the day.
/// </summary>
public class DigestResponse
{
    public required Guid CardiMemberId { get; init; }
    public required DateOnly LocalDate { get; init; }
    public required string Audience { get; init; }

    /// <summary>
    /// A few words naming what this summary is about, for clients that title the summary with it.
    /// Null on summaries generated before headlines existed — clients fall back to their own label.
    /// </summary>
    public string? Headline { get; init; }

    public required string Text { get; init; }

    /// <summary>
    /// Three short ways the family could support this CardiMember today, generated alongside the
    /// text. Null when this generation produced none that survived validation — clients hide the
    /// section rather than rendering a partial set.
    /// </summary>
    public IReadOnlyList<string>? Suggestions { get; init; }

    public required DateTime GeneratedAtUtc { get; init; }
}
