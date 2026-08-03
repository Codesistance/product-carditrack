using System.Reflection;

namespace CardiTrack.Mobile;

/// <summary>
/// Build-time configuration stamped into the assembly by MSBuild (see the AssemblyMetadata
/// items in CardiTrack.Mobile.csproj). Override locally via Local.props or in CI via
/// -p:ApiBaseUrl=... -p:Auth0Domain=... etc.
/// </summary>
public static class AppConfig
{
    public static string ApiBaseUrl { get; } = Read("ApiBaseUrl");
    public static string Auth0Domain { get; } = Read("Auth0Domain");
    public static string Auth0ClientId { get; } = Read("Auth0ClientId");
    public static string Auth0Audience { get; } = Read("Auth0Audience");

    public static void Validate()
    {
        if (string.IsNullOrWhiteSpace(ApiBaseUrl) || !Uri.IsWellFormedUriString(ApiBaseUrl, UriKind.Absolute))
            throw new InvalidOperationException(
                $"ApiBaseUrl is missing or invalid ('{ApiBaseUrl}'). Set it in Local.props or via -p:ApiBaseUrl.");
        // Auth0 values may be legitimately empty in a build that never authenticates
        // (e.g. UI preview); AuthService fails with AuthErrorCode.NotConfigured instead.
    }

    private static string Read(string key) =>
        typeof(AppConfig).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == key)?.Value ?? string.Empty;
}
