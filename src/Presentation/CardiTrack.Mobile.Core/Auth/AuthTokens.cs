namespace CardiTrack.Mobile.Core.Auth;

public sealed record AuthTokens(
    string AccessToken,
    string? RefreshToken,
    string? IdToken,
    DateTimeOffset ExpiresAt);
