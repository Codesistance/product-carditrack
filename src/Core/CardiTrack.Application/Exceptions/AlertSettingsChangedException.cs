namespace CardiTrack.Application.Exceptions;

/// <summary>
/// Raised when a write was made against an alert setting — an alarm, or a member's rule
/// switches — as it stood at one moment, and it no longer stands that way: retuned, switched or
/// removed by someone else in between. The caller proposed a change from a picture that is now
/// wrong, and must re-read rather than write over what changed.
/// </summary>
/// <remarks>
/// Its own type rather than <see cref="InvalidOperationException"/>, for the reason
/// <see cref="AlertStateException"/> gives: the chat answers this with its own sentence, and
/// catching the framework exception would render any incidental one as that sentence.
/// </remarks>
public class AlertSettingsChangedException : Exception
{
    public AlertSettingsChangedException()
        : base("The setting has been changed since it was read.")
    {
    }
}
