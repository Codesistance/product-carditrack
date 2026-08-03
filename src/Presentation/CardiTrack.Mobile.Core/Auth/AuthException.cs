namespace CardiTrack.Mobile.Core.Auth;

public sealed class AuthException : Exception
{
    public AuthErrorCode Code { get; }

    /// <summary>Raw Auth0 "error" value, when the failure came from an Auth0 response.</summary>
    public string? Auth0Error { get; }

    /// <summary>Raw Auth0 "error_description"/policy text — may contain tenant password rules.</summary>
    public string? Auth0Description { get; }

    public AuthException(AuthErrorCode code, string message,
        string? auth0Error = null, string? auth0Description = null, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
        Auth0Error = auth0Error;
        Auth0Description = auth0Description;
    }
}
