namespace CardiTrack.Application.Exceptions;

/// <summary>
/// Raised when an export consent is missing, expired, already used, or does not
/// match the generate request. The API maps it to 400 and returns the message
/// to the caregiver verbatim, so messages here are user-facing copy.
/// </summary>
public class ExportConsentException : Exception
{
    public ExportConsentException(string message)
        : base(message)
    {
    }
}
