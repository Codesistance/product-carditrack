namespace CardiTrack.Application.DTOs.Responses;

/// <summary>
/// PKCE OAuth initiation payload. The client stores State and CodeVerifier locally, opens
/// AuthorizationUrl in a browser, then posts code + state + verifier to the callback endpoint.
/// </summary>
public class OAuthInitiationResponse
{
    public string AuthorizationUrl { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string CodeVerifier { get; set; } = string.Empty;
}
