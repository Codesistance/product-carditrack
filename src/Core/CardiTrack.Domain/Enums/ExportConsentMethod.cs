namespace CardiTrack.Domain.Enums;

/// <summary>
/// How the caregiver proved they meant this export. Password is checked against Auth0;
/// biometric is a device-local unlock on an already signed-in session.
/// </summary>
public enum ExportConsentMethod
{
    Password = 1,
    Biometric = 2
}
