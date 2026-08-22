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
    /// Electrocardiogram readings, and the irregular-rhythm notifications a watch raises on its
    /// own. Two scopes rather than one because Google gates them separately, and both were added
    /// after <see cref="Settings"/> — so, like it, every connection authorised earlier lacks them
    /// until the wearer reconnects.
    /// </summary>
    /// <remarks>
    /// Google classes both as SaMD features. CardiTrack neither performs nor interprets the
    /// measurement: it reads what the wearer's own device already decided and told them, and
    /// relays it to the family who would otherwise hear about it only if the wearer mentioned it.
    /// </remarks>
    public const string Ecg = "ecg";
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
    /// Whether the granted set covers either rhythm data type. One question rather than two
    /// because the caller's decision is a single one — whether to spend a request on the rhythm
    /// read at all — and the read itself tolerates the half-granted case, counting whichever of
    /// the two it can see and leaving the other null.
    /// </summary>
    public static bool GrantsRhythm(IEnumerable<string> scopes) =>
        scopes.Any(s => Normalise(s) is Ecg or Irn);
}
