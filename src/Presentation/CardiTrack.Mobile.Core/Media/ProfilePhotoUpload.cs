using CardiTrack.Application.Services;

namespace CardiTrack.Mobile.Core.Media;

/// <summary>
/// Turns a picked or captured image file into the <c>photoBase64</c> payload the API accepts
/// (JPEG/PNG, at most <see cref="ProfilePhotoBase64.MaxDecodedBytes"/> decoded — the same
/// constant the server's validators enforce, referenced rather than restated so the client
/// cannot drift from the contract).
///
/// The preferred path re-encodes on device: a phone camera's 12-megapixel original is
/// ~4000 px across, and the server throws away everything past 1024 px anyway, so shipping
/// the full frame would spend the caregiver's upload allowance on pixels that never land.
/// When the platform can't decode the file, the raw bytes still go up as-is if they are
/// something the server can decode — its codec is broader than any one device's.
/// </summary>
public static class ProfilePhotoUpload
{
    /// <summary>
    /// The on-device downscale ceiling for the longest edge. Above the server's 1024 px
    /// stored size on purpose: the server is the authority on the final size, and handing
    /// it a little headroom beats pre-empting it with a second, competing crop policy.
    /// </summary>
    public const int MaxEdge = 1280;

    /// <summary>
    /// Prepares <paramref name="original"/> for upload. Never throws for a bad image —
    /// a photo that can't be prepared comes back as a
    /// <see cref="ProfilePhotoUploadResult.Failed"/> sentence for the form to show.
    /// </summary>
    public static async Task<ProfilePhotoUploadResult> PrepareAsync(
        byte[] original, IProfilePhotoTranscoder transcoder, CancellationToken ct = default)
    {
        byte[]? transcoded;
        try
        {
            transcoded = await transcoder.TryTranscodeAsync(original, MaxEdge, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // A platform codec crashing on exotic bytes is the same answer as "can't
            // decode" — fall through to the raw-bytes path rather than losing the form.
            transcoded = null;
        }

        if (transcoded is { Length: > 0 } && transcoded.Length <= ProfilePhotoBase64.MaxDecodedBytes)
            return ProfilePhotoUploadResult.Ready(Convert.ToBase64String(transcoded));

        if (!LooksLikeJpegOrPng(original))
            return ProfilePhotoUploadResult.Failed(
                "We couldn't read that image. Try a JPEG or PNG photo instead.");

        if (original.Length > ProfilePhotoBase64.MaxDecodedBytes)
            return ProfilePhotoUploadResult.Failed(
                "That photo is larger than 5 MB. Try a smaller one.");

        return ProfilePhotoUploadResult.Ready(Convert.ToBase64String(original));
    }

    /// <summary>
    /// Magic-byte sniff for the two formats the API accepts. A gate on the raw-bytes
    /// fallback only — the server content-sniffs again and is the authority.
    /// </summary>
    public static bool LooksLikeJpegOrPng(byte[] bytes) => IsJpeg(bytes) || IsPng(bytes);

    private static bool IsJpeg(byte[] b) =>
        b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF;

    private static bool IsPng(byte[] b) =>
        b.Length >= 8
        && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47
        && b[4] == 0x0D && b[5] == 0x0A && b[6] == 0x1A && b[7] == 0x0A;
}
