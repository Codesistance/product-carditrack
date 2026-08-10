namespace CardiTrack.Application.DTOs.Responses;

/// <summary>
/// One daily digest as the apps read it. <c>LocalDate</c> is the member's local calendar day the
/// text describes — already local, so clients render it without timezone arithmetic.
/// </summary>
public class DigestResponse
{
    public required Guid CardiMemberId { get; init; }
    public required DateOnly LocalDate { get; init; }
    public required string Audience { get; init; }
    public required string Text { get; init; }
    public required DateTime GeneratedAtUtc { get; init; }
}
