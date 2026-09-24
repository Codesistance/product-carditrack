namespace CardiTrack.Mobile.Core.Diagnostics;

/// <summary>
/// Per-feature intake URLs for a Datadog site the mobile SDK cannot name itself. The native
/// SDKs use a custom endpoint verbatim — nothing is appended — so each feature needs its own
/// full URL; a bare host posts every batch to the site root and gets a 404.
/// </summary>
public sealed class DatadogIntake
{
    public string Host { get; }

    public string Rum => $"https://{Host}/api/v2/rum";

    public string Logs => $"https://{Host}/api/v2/logs";

    public string Traces => $"https://{Host}/api/v2/spans";

    private DatadogIntake(string host) => Host = host;

    /// <summary>
    /// Accepts a bare DNS host name only: a scheme, path or port would double up with the
    /// URL composed here, and misrouted telemetry is worse than none.
    /// </summary>
    public static bool TryCreate(string? host, out DatadogIntake? intake)
    {
        intake = null;
        var trimmed = host?.Trim();
        if (string.IsNullOrEmpty(trimmed) || Uri.CheckHostName(trimmed) != UriHostNameType.Dns)
            return false;

        intake = new DatadogIntake(trimmed);
        return true;
    }
}
