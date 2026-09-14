namespace CardiTrack.Application.Interfaces.Clients;

/// <summary>
/// Blob storage for CardiMember profile photos. A full-face photo is Tier 1 data (Safe Harbor
/// category 17 — docs/technical/data_protection_architecture.md), which drives this port's shape:
/// callers hold an opaque object name, never a durable URL, and read surfaces exchange the name
/// for a short-lived signed URL at render time. Implementations live in Infrastructure; the only
/// production one is <c>GcsProfilePhotoStorage</c>.
/// </summary>
public interface IProfilePhotoStorage
{
    /// <summary>
    /// Stores an already-processed JPEG (see <see cref="Services.IProfilePhotoProcessor"/> — raw
    /// uploads never reach storage) and returns the object name it was stored under, in the form
    /// <c>members/{cardiMemberId}/{guid:N}.jpg</c>. Every upload gets a fresh name, so replacing a
    /// photo can delete the old object only after the new name is safely saved.
    /// </summary>
    /// <exception cref="InvalidOperationException">Photo storage is not configured (no bucket).</exception>
    Task<string> UploadAsync(Guid cardiMemberId, ReadOnlyMemory<byte> jpegBytes, CancellationToken ct = default);

    /// <summary>
    /// Deletes a stored photo. An already-missing object counts as success; other storage faults
    /// propagate — callers on best-effort cleanup paths catch and continue, so a blob that could
    /// not be deleted today is retried by nothing but never fails the save it followed.
    /// </summary>
    /// <exception cref="InvalidOperationException">Photo storage is not configured (no bucket).</exception>
    Task DeleteAsync(string objectName, CancellationToken ct = default);

    /// <summary>
    /// Deletes every object stored for this member, not only the one their row currently names.
    /// Returns the object names it could not remove.
    /// </summary>
    /// <remarks>
    /// Erasure needs this and <see cref="DeleteAsync"/> cannot give it. Every upload gets a fresh
    /// name, so a replacement interrupted between writing the new object and saving the new name
    /// leaves a second face photo under the same member — reachable by nobody, and invisible to a
    /// cleanup keyed on what the row points at. Deleting only the referenced object would leave a
    /// photograph of an erased person's face in the bucket.
    /// </remarks>
    Task<IReadOnlyList<string>> DeleteAllForMemberAsync(Guid cardiMemberId, CancellationToken ct = default);

    /// <summary>
    /// A short-lived signed GET URL for a stored photo, or null when storage is not configured or
    /// signing is unavailable — read surfaces degrade to the initials avatar, they never throw.
    /// The URL is a bearer capability: callers must not persist or log it.
    /// </summary>
    Task<string?> GetReadUrlAsync(string objectName, CancellationToken ct = default);

    /// <summary>
    /// Every stored photo object with its creation time — the enforcement backstop's view of the
    /// bucket (<c>OrphanedPhotoCleanupWorker</c> diffs it against the live
    /// <see cref="Domain.Entities.CardiMember.PhotoObjectName"/> set). Streamed rather than
    /// materialized because the bucket has no natural upper bound. An unset bucket yields an
    /// empty sequence with the adapter's usual warn-once, so an unconfigured host sweeps nothing
    /// rather than throwing.
    /// </summary>
    IAsyncEnumerable<(string ObjectName, DateTimeOffset CreatedAt)> ListAsync(CancellationToken ct = default);
}
