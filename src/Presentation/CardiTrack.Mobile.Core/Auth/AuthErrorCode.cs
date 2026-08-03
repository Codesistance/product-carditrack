namespace CardiTrack.Mobile.Core.Auth;

public enum AuthErrorCode
{
    InvalidCredentials,
    TooManyAttempts,
    UserAlreadyExists,
    WeakPassword,
    NotConfigured,
    Network,
    SessionExpired,
    Unknown,
}
