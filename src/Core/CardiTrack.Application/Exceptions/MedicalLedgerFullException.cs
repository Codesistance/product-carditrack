namespace CardiTrack.Application.Exceptions;

/// <summary>
/// Raised when a change to a member's medical information would make its summary longer than the
/// single note it stands in for may be (see <c>MedicalLedger.MaxSummaryLength</c>). The API maps it
/// to 400 and returns the message verbatim, so it is user-facing copy, never internals.
/// </summary>
/// <remarks>
/// Its own type for the reason <see cref="InvalidProfilePhotoException"/> gives: catching
/// <see cref="InvalidOperationException"/> would also catch an encryption or persistence fault
/// from the same call and hand it to the caregiver as a 400, hiding a server fault from the 5xx it
/// should have raised.
/// </remarks>
public class MedicalLedgerFullException : Exception
{
    public MedicalLedgerFullException(string message)
        : base(message)
    {
    }
}
