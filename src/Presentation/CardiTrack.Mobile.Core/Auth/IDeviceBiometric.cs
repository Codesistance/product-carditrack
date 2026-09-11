namespace CardiTrack.Mobile.Core.Auth;

/// <summary>
/// Device-local fingerprint / face unlock. Not Auth0 biometric login — that is R4.
/// A stolen session can still attest; prefer <see cref="IAuthService.VerifyPasswordAsync"/>
/// when the account has a password.
/// </summary>
public interface IDeviceBiometric
{
    /// <summary>The OS has a fingerprint or face enrolled and can prompt for it.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// The device can do biometrics but nothing is enrolled yet — the caregiver
    /// can be sent to the OS settings to turn it on.
    /// </summary>
    bool CanEnroll { get; }

    Task<bool> AuthenticateAsync(string reason, CancellationToken ct = default);

    /// <summary>Opens the OS screen where fingerprint or face unlock is turned on.</summary>
    Task OpenEnrollmentSettingsAsync();
}
