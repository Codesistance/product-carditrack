namespace CardiTrack.Application.Exceptions;

/// <summary>
/// Raised when account provisioning finds the email already owned by a different Auth0
/// identity — the tenant-side account linking either hasn't run or was skipped (see
/// docs/technical/auth0_setup_runbook.md §8). Maps to 409 in ExceptionHandlingMiddleware.
/// </summary>
public class DuplicateEmailException : Exception
{
    public DuplicateEmailException(string message) : base(message)
    {
    }
}
