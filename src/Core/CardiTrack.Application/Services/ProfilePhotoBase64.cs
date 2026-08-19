namespace CardiTrack.Application.Services;

/// <summary>
/// Decodes the <c>photoBase64</c> request field. One decoder shared by the validators and
/// <see cref="CardiMemberService"/>, so what the validator accepts is exactly what the service
/// can decode — the two drifting apart would turn a 400 into a 500 or vice versa.
/// </summary>
public static class ProfilePhotoBase64
{
    /// <summary>
    /// The published upload cap: 5 MB of decoded image bytes. The API contract
    /// (docs/execution/backend/api/cardimembers.md) states it, the validators enforce it
    /// pre-decode, and the processor re-checks it — one constant so they cannot disagree.
    /// </summary>
    public const int MaxDecodedBytes = 5 * 1024 * 1024;

    /// <summary>
    /// Decodes, tolerating the <c>data:image/…;base64,</c> prefix the documented examples carry —
    /// clients that paste a data URI straight from a picker must not be rejected on a technicality.
    /// Returns false for anything that is not valid base64; emptiness and the size cap are the
    /// caller's rules to state, with their own messages.
    /// </summary>
    public static bool TryDecode(string? value, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var payload = value.AsSpan().Trim();
        if (payload.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = payload.IndexOf(',');
            if (comma < 0)
                return false;
            payload = payload[(comma + 1)..];
        }

        // Base64 decodes to at most 3/4 of its character count, so this buffer always suffices.
        var buffer = new byte[(payload.Length >> 2) * 3 + 3];
        if (!Convert.TryFromBase64Chars(payload, buffer, out var written))
            return false;

        bytes = buffer.AsSpan(0, written).ToArray();
        return true;
    }
}
