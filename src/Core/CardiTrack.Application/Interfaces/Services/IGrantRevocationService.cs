namespace CardiTrack.Application.Interfaces.Services;

/// <summary>
/// Ends the provider grants queued in <c>PendingGrantRevocations</c>: the grants of removed and
/// replaced devices, and of grants refused after the code exchange.
/// </summary>
public interface IGrantRevocationService
{
    /// <summary>
    /// Works through the revocations due by <paramref name="utcNow"/>. Returns how many grants were
    /// confirmed ended.
    /// </summary>
    Task<int> RevokeDueAsync(DateTime utcNow, CancellationToken ct = default);
}
