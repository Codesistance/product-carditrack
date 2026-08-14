namespace CardiTrack.Application.Exceptions;

/// <summary>
/// Raised when an alert is in a state that refuses the requested transition — un-acknowledging one
/// the system has already resolved being the only case today.
/// </summary>
/// <remarks>
/// Its own type rather than <see cref="InvalidOperationException"/>, because the API returns the
/// message to the caregiver verbatim. Catching the framework exception would mean any incidental
/// one from the same call — an EF tracking fault, a bad cast — was rendered as user-facing copy
/// and answered 400, hiding a genuine server fault from the 5xx it should have raised.
/// </remarks>
public class AlertStateException : Exception
{
    public AlertStateException(string message)
        : base(message)
    {
    }
}
