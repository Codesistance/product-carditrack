namespace CardiTrack.Mobile.Core.Auth;

public enum AuthErrorCode
{
    InvalidCredentials,
    TooManyAttempts,
    UserAlreadyExists,
    WeakPassword,
    /// <summary>Login denied by the tenant's post-login Action until the email is verified.</summary>
    EmailNotVerified,
    /// <summary>The requested social connection isn't enabled on the tenant (e.g. Apple before its credentials are provisioned).</summary>
    ProviderUnavailable,
    NotConfigured,
    Network,
    SessionExpired,
    Unknown,
}
