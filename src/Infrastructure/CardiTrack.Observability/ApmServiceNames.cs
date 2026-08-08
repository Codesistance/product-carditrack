namespace CardiTrack.Observability;

/// <summary>
/// What each host calls itself to the APM backend: the app type, lowercase. This is the
/// "service" dimension — the one every backend groups, filters and alerts on — so the
/// values are deliberately bare ("api", not "CardiTrack.API"): the org is CardiTrack,
/// the service is the app within it.
///
/// One constant per host, used for BOTH signals — the log sink's service field and the
/// OTel resource's service.name. They must agree: Datadog joins a log to its trace on
/// service, so a log shipped as "api" against spans resolved as "CardiTrack.API" would
/// correlate with nothing.
/// </summary>
public static class ApmServiceNames
{
    public const string Api = "api";
    public const string Web = "web";
    public const string Worker = "worker";

    /// <summary>Every backend service name, for tests and diagnostics.</summary>
    public static IReadOnlyList<string> All { get; } = [Api, Web, Worker];
}
