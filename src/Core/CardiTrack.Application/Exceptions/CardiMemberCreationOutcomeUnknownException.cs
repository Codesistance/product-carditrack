namespace CardiTrack.Application.Exceptions;

/// <summary>
/// Raised when a CardiMember creation failed at or after the point its transaction was asked
/// to commit, so the caller cannot know whether the member exists.
/// </summary>
/// <remarks>
/// A commit can be accepted by the database and the call still fail on the way back — an
/// acknowledgement lost to a dropped connection, a fault while the transaction is disposed. On
/// that path the member row may well exist, and the one thing the caller still needs is its id:
/// the audit entry for the request must be able to name the member, and a reconciler must be
/// able to find the row. A failure <em>before</em> the commit was attempted is rolled back and
/// rethrown as itself; only the indeterminate outcome takes this shape.
/// </remarks>
public class CardiMemberCreationOutcomeUnknownException : Exception
{
    public CardiMemberCreationOutcomeUnknownException(Guid cardiMemberId, Exception innerException)
        : base(
            "The CardiMember creation's commit did not complete cleanly; the member may or may not exist.",
            innerException)
    {
        CardiMemberId = cardiMemberId;
    }

    /// <summary>The id the member was created under — the row to look for.</summary>
    public Guid CardiMemberId { get; }
}
