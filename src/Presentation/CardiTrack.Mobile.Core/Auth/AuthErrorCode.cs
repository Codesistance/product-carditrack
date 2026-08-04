namespace CardiTrack.Mobile.Core.Auth;

public enum AuthErrorCode
{
    InvalidCredentials,
    TooManyAttempts,
    UserAlreadyExists,
    WeakPassword,
    /// <summary>Login denied by the tenant's post-login Action until the email is verified.</summary>
    EmailNotVerified,
    NotConfigured,
    Network,
    SessionExpired,
    Unknown,
}
