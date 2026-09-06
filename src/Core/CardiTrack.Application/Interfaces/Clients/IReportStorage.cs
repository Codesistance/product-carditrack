namespace CardiTrack.Application.Interfaces.Clients;

/// <summary>
/// Blob storage for rendered health-data exports. Implementations live in Infrastructure; the only
/// production one is <c>GcsReportStorage</c>.
/// </summary>
/// <remarks>
/// <para>
/// Shaped deliberately unlike <see cref="IProfilePhotoStorage"/>, which it otherwise mirrors: there
/// is no signed-URL method. An export is a full identified health record, and a signed URL is a
/// bearer capability to it — anyone holding the link gets the file, outside our authorization and
/// invisible to the audit trail. Downloads therefore stream back through the API, which keeps the
/// ownership check and the <c>[AuditHealthDataAccess]</c> row on every retrieval. Reads return the
/// bytes; the cost of proxying them is the price of knowing who read whose record.
/// </para>
/// <para>
/// An unconfigured bucket is the feature's off switch, and unlike profile photos it fails loudly on
/// every operation: there is no graceful degradation for an export — a caregiver either gets the
/// document they asked for or is told it could not be produced.
/// </para>
/// </remarks>
public interface IReportStorage
{
    /// <summary>
    /// Stores rendered export bytes and returns the object name they were stored under, in the
    /// form <c>reports/{ownerUserId}/{reportId}.{extension}</c>. Owner-prefixed so a bucket
    /// listing during an erasure sweep can find everything belonging to one account.
    /// </summary>
    /// <exception cref="InvalidOperationException">Report storage is not configured (no bucket).</exception>
    Task<string> UploadAsync(
        Guid ownerUserId,
        string reportId,
        string extension,
        string contentType,
        ReadOnlyMemory<byte> content,
        CancellationToken ct = default);

    /// <summary>
    /// Reads stored export bytes back, or null when the object is gone — a report whose row
    /// outlived its object reads as expired rather than as a server error.
    /// </summary>
    /// <exception cref="InvalidOperationException">Report storage is not configured (no bucket).</exception>
    Task<byte[]?> DownloadAsync(string objectName, CancellationToken ct = default);

    /// <summary>
    /// Deletes a stored export. An already-missing object counts as success, so a retried
    /// cleanup sweep is idempotent; other storage faults propagate to the caller's error boundary.
    /// </summary>
    /// <exception cref="InvalidOperationException">Report storage is not configured (no bucket).</exception>
    Task DeleteAsync(string objectName, CancellationToken ct = default);
}
