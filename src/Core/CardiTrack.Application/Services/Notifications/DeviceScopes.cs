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

    /// <summary>
    /// Paired-device telemetry — battery level and status. Added after the three read bundles, so
    /// every connection authorised before it shipped lacks it until the wearer reconnects.
    /// </summary>
    public const string Settings = "settings";

    /// <summary>
    /// Wearer-initiated ECG readings. A restricted scope Google classes as an SaMD feature, so it
    /// is granted only where the project has passed verification for it.
    /// </summary>
    public const string Ecg = "ecg";

    /// <summary>
    /// Irregular-rhythm notifications — the device's own passive AFib screening. Restricted and
    /// SaMD-classed like <see cref="Ecg"/>, and requested alongside it.
    /// </summary>
    public const string Irn = "irn";

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

    /// <summary>
    /// Whether the granted set covers paired-device telemetry. False for every connection made
    /// before <see cref="Settings"/> was added to the requested scopes — which is why a missing
    /// battery reading is never treated as a fault.
    /// </summary>
    public static bool GrantsSettings(IEnumerable<string> scopes) =>
        scopes.Any(s => Normalise(s) == Settings);

    /// <summary>
    /// Whether the granted set covers either rhythm data type. Both are restricted scopes Google
    /// classes as SaMD features, so a connection carries them only where the wearer authorised
    /// after they shipped <em>and</em> the project passed verification for them — which makes
    /// false the expected answer for most connections rather than a fault.
    /// </summary>
    /// <remarks>
    /// One gate for the pair rather than two, because the read behind it fetches both and each
    /// half tolerates its own absence: a wearer who granted ECG but not IRN gets ECG counts and
    /// nulls for the rest, which is exactly what <c>DeviceRhythmDay</c>'s null-versus-zero rule
    /// exists for. Two gates would save a request only for the wearer who granted exactly one.
    /// </remarks>
    public static bool GrantsRhythm(IEnumerable<string> scopes) =>
        scopes.Any(s => Normalise(s) is Ecg or Irn);
}
