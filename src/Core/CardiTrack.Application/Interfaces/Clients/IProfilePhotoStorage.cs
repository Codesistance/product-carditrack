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
    /// A short-lived signed GET URL for a stored photo, or null when storage is not configured or
    /// signing is unavailable — read surfaces degrade to the initials avatar, they never throw.
    /// The URL is a bearer capability: callers must not persist or log it.
    /// </summary>
    Task<string?> GetReadUrlAsync(string objectName, CancellationToken ct = default);
}
