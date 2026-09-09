namespace CardiTrack.Application.DTOs.Responses;

public class ExportConsentResponse
{
    public required string ConsentToken { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
}
