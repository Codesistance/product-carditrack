using System.Security.Cryptography;
using System.Text;
using CardiTrack.Application.Interfaces.Security;

namespace CardiTrack.Infrastructure.Security;

/// <summary>
/// The shared key in front of <c>POST /api/v1/mobile/diagnostics/logs</c>. One value, compiled
/// into every store build and read by the API from Secret Manager (<c>MobileDiagnostics:Key</c>);
/// a request either presents it exactly or is refused.
/// </summary>
/// <remarks>
/// <para>
/// This is an abuse limiter, not a secret in the cryptographic sense: anything compiled into a
/// public app binary can be read out of it. What it buys is that the endpoint is not open to
/// the whole internet by URL alone — a scanner has to have pulled the app apart first — and
/// the rate limit on the route bounds what even a leaked key can do. Nothing the endpoint
/// accepts is trusted beyond being written to our own logs under the mobile service name.
/// </para>
/// <para>
/// Unconfigured is a first-class state, not an error: a build with no key (a developer's
/// machine, an environment whose Terraform has not applied yet) must make the endpoint
/// disappear rather than accept anything. <see cref="Matches"/> is therefore false for every
/// input when <see cref="IsConfigured"/> is false, and the controller answers 404 first.
/// </para>
/// </remarks>
public sealed class MobileDiagnosticsKey : IMobileDiagnosticsKey
{
    /// <summary>
    /// A shorter value is treated as unset. Terraform issues 32 random bytes as base64 (44
    /// characters); anything under this is a typo or a test value that must not gate a live
    /// endpoint.
    /// </summary>
    public const int MinimumLength = 16;

    /// <summary>Terraform's "no value yet" marker, the same one <c>ApmOptions</c> refuses.</summary>
    private const string Placeholder = "REPLACE_ME";

    private readonly byte[]? _key;

    private MobileDiagnosticsKey(byte[]? key) => _key = key;

    /// <summary>The unconfigured instance: refuses everything.</summary>
    public static MobileDiagnosticsKey Disabled { get; } = new(null);

    public bool IsConfigured => _key is not null;

    public static MobileDiagnosticsKey FromConfiguration(string? configured)
    {
        var value = configured?.Trim();
        if (string.IsNullOrEmpty(value)
            || string.Equals(value, Placeholder, StringComparison.Ordinal)
            || value.Length < MinimumLength)
        {
            return Disabled;
        }

        return new MobileDiagnosticsKey(Encoding.UTF8.GetBytes(value));
    }

    /// <summary>
    /// Constant-time over the whole value. Length still leaks through the equality of lengths,
    /// which is fine here — the key's length is fixed by how Terraform mints it and is no secret.
    /// </summary>
    public bool Matches(string? presented)
    {
        if (_key is null || string.IsNullOrWhiteSpace(presented))
            return false;

        var candidate = Encoding.UTF8.GetBytes(presented.Trim());
        return candidate.Length == _key.Length && CryptographicOperations.FixedTimeEquals(candidate, _key);
    }
}
