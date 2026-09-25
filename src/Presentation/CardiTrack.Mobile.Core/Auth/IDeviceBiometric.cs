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

    /// <summary>Asks for the fingerprint or face.</summary>
    /// <param name="title">
    /// What is being confirmed, as the prompt's heading — "Delete your account". Android shows it;
    /// iOS has no heading of its own (it names the app there) and shows only the description.
    /// </param>
    /// <param name="description">
    /// Why the phone is asking, in one sentence that does not repeat the title. Android shows it
    /// under the heading; on iOS it is the whole of the prompt's own text.
    /// </param>
    Task<bool> AuthenticateAsync(string title, string description, CancellationToken ct = default);

    /// <summary>Opens the OS screen where fingerprint or face unlock is turned on.</summary>
    Task<bool> OpenEnrollmentSettingsAsync();
}
