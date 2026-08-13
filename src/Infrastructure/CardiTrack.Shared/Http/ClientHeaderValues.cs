namespace CardiTrack.Shared.Http;

/// <summary>
/// What a <see cref="ClientHeaderNames"/> value is allowed to look like. Both ends depend on this
/// rather than each keeping its own rule: the mobile client refuses to send a value the API would
/// only throw away, and the API stays free to reject without that becoming a silent divergence.
///
/// The API's use is the load-bearing one. These headers arrive unauthenticated on a public
/// endpoint and end up in log lines and span tags, so an unconstrained value would let a caller
/// forge log structure or mint unbounded tag cardinality in the APM backend. The charset below is
/// everything a version string or platform name legitimately needs and nothing that can do either.
/// </summary>
public static class ClientHeaderValues
{
    /// <summary>
    /// Generous for "1.4.2-rc.1+37" and far short of anything worth storing or indexing. Values
    /// are rejected at this length rather than truncated — a clipped version string reads like a
    /// real one and would quietly mislead whoever queries it.
    /// </summary>
    public const int MaxLength = 64;

    /// <summary>
    /// True when <paramref name="value"/> is safe to record as-is. Null, empty, over-long, and
    /// anything outside the charset are all simply "no value" — a malformed header is never worth
    /// failing a health-data request over.
    /// </summary>
    public static bool IsValid(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxLength)
            return false;

        foreach (var character in value)
        {
            if (!IsAllowed(character))
                return false;
        }

        return true;
    }

    /// <summary>
    /// ASCII alphanumerics plus the four punctuation marks semver uses. Notably excluded:
    /// whitespace and control characters (which could forge a second log line), and every
    /// non-ASCII character (which could otherwise smuggle homoglyphs past a log reader).
    /// </summary>
    private static bool IsAllowed(char character) =>
        char.IsAsciiLetterOrDigit(character) || character is '.' or '+' or '-' or '_';
}
