namespace CardiTrack.Application.Exceptions;

/// <summary>
/// Raised when an uploaded profile photo is refused — not a real JPEG/PNG, too large, or
/// otherwise undecodable. The API maps it to 400 and returns the message to the caregiver
/// verbatim, so messages here are user-facing copy, never internals.
/// </summary>
/// <remarks>
/// Its own type for the same reason as <see cref="AlertStateException"/>: catching a framework
/// exception (<see cref="FormatException"/>, <see cref="ArgumentException"/>) would also catch
/// incidental ones from the same call and render them as user-facing copy with a 4xx, hiding a
/// genuine server fault from the 5xx it should have raised.
/// </remarks>
public class InvalidProfilePhotoException : Exception
{
    public InvalidProfilePhotoException(string message)
        : base(message)
    {
    }

    public InvalidProfilePhotoException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
