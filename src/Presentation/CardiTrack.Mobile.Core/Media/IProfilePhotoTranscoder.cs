namespace CardiTrack.Mobile.Core.Media;

/// <summary>
/// The platform image codec behind <see cref="ProfilePhotoUpload"/>. Decodes a picked or
/// captured image, shrinks it so its longest edge is at most <paramref name="maxEdge"/>
/// pixels (never upscaling), and re-encodes it as JPEG. Lives behind an interface because
/// decoding needs platform APIs the testable core deliberately doesn't reference.
/// </summary>
public interface IProfilePhotoTranscoder
{
    /// <summary>
    /// The re-encoded JPEG bytes, or null when the source bytes cannot be decoded as an
    /// image on this platform. Null is an answer, not an error — the policy layer decides
    /// what an undecodable file means.
    /// </summary>
    Task<byte[]?> TryTranscodeAsync(byte[] source, int maxEdge, CancellationToken ct = default);
}
