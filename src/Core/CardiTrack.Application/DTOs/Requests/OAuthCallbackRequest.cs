namespace CardiTrack.Application.DTOs.Requests;

/// <summary>
/// Completes a device OAuth flow (POST /api/v1/oauth/callback/{provider}). The code verifier
/// travels in the authenticated POST body — never as a query parameter.
/// </summary>
public class OAuthCallbackRequest
{
    public string Code { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string CodeVerifier { get; set; } = string.Empty;
}
