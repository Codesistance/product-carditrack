namespace CardiTrack.Application.Exceptions;

/// <summary>
/// Raised when a caregiver's app sends a canned response code this alert's rule does not offer.
/// </summary>
/// <remarks>
/// <para>
/// Its own type, and carrying <see cref="ValidCodes"/>, because the answer a client needs here is
/// not "no" but "not that one — these". The client's chip list and the server's catalogue are two
/// copies of the same fact and an app that has not been updated in a while will hold a stale one;
/// naming the live codes in the rejection is what lets it recover rather than silently failing
/// every attempt to answer an alert.
/// </para>
/// <para>
/// Distinct from <see cref="AlertStateException"/>, which is about the alert refusing a
/// transition. This one is about the request, and the alert is fine.
/// </para>
/// </remarks>
public class AlertResponseCodeException : Exception
{
    public AlertResponseCodeException(string message, IReadOnlyList<string> validCodes)
        : base(message)
    {
        ValidCodes = validCodes;
    }

    /// <summary>What this rule does offer, for the client to re-sync against.</summary>
    public IReadOnlyList<string> ValidCodes { get; }
}
