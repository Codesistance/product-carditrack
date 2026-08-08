using System.Reflection;
using CardiTrack.Shared;

namespace CardiTrack.Observability;

/// <summary>
/// Which build is running. The release version is the semver tag CI computes for the
/// deploy: the tag names the image as-is (v1.2.3), while the Dockerfile's VERSION build
/// arg gets it without the "v" (1.2.3), because MSBuild rejects a v-prefixed Version.
/// So an image tagged v1.2.3 reports 1.2.3 — the same release, one character apart.
/// Every host attaches it to its logs (the "Version" property) and to its OTel resource
/// (service.version), so a spike in errors or latency can be pinned to a release.
///
/// Anything not built by the release pipeline reports "0.0.0-local" — the default
/// &lt;Version&gt; carried by this project and the hosts — so an unstamped build is visible
/// as such rather than masquerading as a release.
/// </summary>
public static class DeploymentInfo
{
    /// <summary>Reported when the entry assembly carries no version at all (e.g. a test host).</summary>
    public const string UnknownVersion = "unknown";

    // This assembly, not the entry assembly: -p:Version= flows to project references, so
    // both carry the same stamp — but the entry assembly is not always ours. Under the
    // migrator job it is the dotnet-ef tool, which would report EF's version instead.
    private static readonly Lazy<string> Resolved = new(() => Resolve(
        Environment.GetEnvironmentVariable(ConfigurationKeys.Deployment.Version),
        typeof(DeploymentInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion));

    /// <summary>The running build's version, resolved once per process.</summary>
    public static string Version => Resolved.Value;

    /// <summary>
    /// Resolution order: the DEPLOY_VERSION override, then the assembly's informational
    /// version (what -p:Version= stamps), then <see cref="UnknownVersion"/>. Takes the
    /// strings rather than the assembly so the precedence is testable; callers want
    /// <see cref="Version"/>.
    /// </summary>
    public static string Resolve(string? overrideValue, string? informationalVersion)
    {
        if (!string.IsNullOrWhiteSpace(overrideValue))
            return Clean(overrideValue);

        return string.IsNullOrWhiteSpace(informationalVersion)
            ? UnknownVersion
            : Clean(informationalVersion);
    }

    /// <summary>
    /// Reduces a version to the bare semver APM backends key release comparisons on:
    /// drops the "+&lt;sha&gt;" build metadata the SDK appends when a source revision is
    /// known, and the leading "v" of a tag-shaped value. CI already strips that "v" before
    /// the build arg; doing it here too means a DEPLOY_VERSION override can be a pasted tag.
    /// </summary>
    private static string Clean(string value)
    {
        var trimmed = value.Trim();

        var plus = trimmed.IndexOf('+');
        if (plus >= 0)
            trimmed = trimmed[..plus];

        if (trimmed.StartsWith('v') && trimmed.Length > 1 && char.IsDigit(trimmed[1]))
            trimmed = trimmed[1..];

        return trimmed.Length == 0 ? UnknownVersion : trimmed;
    }
}
