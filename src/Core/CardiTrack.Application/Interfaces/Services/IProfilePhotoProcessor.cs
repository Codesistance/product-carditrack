namespace CardiTrack.Application.Interfaces.Services;

/// <summary>
/// Normalises a caregiver-uploaded profile photo before it may be stored: content-sniffs the
/// format (JPEG/PNG only, whatever the client claimed), bounds the input size and dimensions,
/// downscales, and re-encodes as JPEG with every metadata block stripped — EXIF GPS coordinates
/// in a phone photo would otherwise put a home address next to a full-face image in the bucket.
/// The output is always a fresh encode, never the caller's bytes.
/// </summary>
public interface IProfilePhotoProcessor
{
    /// <summary>Returns the processed JPEG bytes.</summary>
    /// <exception cref="Exceptions.InvalidProfilePhotoException">
    /// The bytes are not a real JPEG/PNG, exceed the size cap, or exceed the dimension cap.
    /// The message is written for the caregiver and is safe to return verbatim.
    /// </exception>
    byte[] Process(ReadOnlyMemory<byte> uploadBytes);
}
