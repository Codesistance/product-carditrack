namespace CardiTrack.Application.Services.Notifications;

/// <summary>
/// Reduces a granted OAuth scope to its bundle name, so rules can ask "was sleep granted?" without
/// knowing whether the connection predates the Google Health API migration.
/// </summary>
/// <remarks>
/// <c>https://www.googleapis.com/auth/googlehealth.sleep.readonly</c>, <c>googlehealth.sleep.readonly</c>
/// and the legacy Fitbit Web API's bare <c>sleep</c> all normalise to <c>sleep</c>.
/// <para>
/// This duplicates the private <c>Normalise</c> in <c>CardiTrack.Mobile.Core/Devices/DeviceDatasets.cs</c>.
/// Mobile.Core references Application, so the two should converge on this copy — tracked as a
/// follow-up rather than done here, since moving it touches the shipped device-management screen.
/// </para>
/// </remarks>
public static class DeviceScopes
{
    public const string Sleep = "sleep";
    public const string ActivityAndFitness = "activity_and_fitness";
    public const string HealthMetrics = "health_metrics_and_measurements";

    public static string Normalise(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
            return string.Empty;

        var value = scope.Trim();

        var lastSlash = value.LastIndexOf('/');
        if (lastSlash >= 0)
            value = value[(lastSlash + 1)..];

        value = value.ToLowerInvariant();

        const string vendorPrefix = "googlehealth.";
        if (value.StartsWith(vendorPrefix, StringComparison.Ordinal))
            value = value[vendorPrefix.Length..];

        const string readonlySuffix = ".readonly";
        if (value.EndsWith(readonlySuffix, StringComparison.Ordinal))
            value = value[..^readonlySuffix.Length];

        return value;
    }

    /// <summary>
    /// Whether the granted set covers sleep. The legacy short name and the Google bundle both
    /// normalise to <see cref="Sleep"/>, so one comparison covers connections of either vintage.
    /// </summary>
    public static bool GrantsSleep(IEnumerable<string> scopes) =>
        scopes.Any(s => Normalise(s) == Sleep);
}
