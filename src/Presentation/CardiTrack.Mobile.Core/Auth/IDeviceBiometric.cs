namespace CardiTrack.Mobile.Core.Auth;

/// <summary>
/// Device-local fingerprint / face unlock. Not Auth0 biometric login — that is R4.
/// A stolen session can still attest; prefer <see cref="IAuthService.VerifyPasswordAsync"/>
/// when the account has a password.
/// </summary>
public interface IDeviceBiometric
{
    bool IsAvailable { get; }

    Task<bool> AuthenticateAsync(string reason, CancellationToken ct = default);
}
