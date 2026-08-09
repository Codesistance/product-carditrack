namespace CardiTrack.Shared.Telemetry;

/// <summary>
/// Names of the solution's own telemetry sources. OpenTelemetry subscribes to
/// ActivitySources and Meters by name, so the project that defines an instrument
/// (CardiTrack.Infrastructure) and the project that registers it for export
/// (CardiTrack.Observability) must agree on the string without referencing each
/// other — this class is the one place both can depend on.
/// </summary>
public static class TelemetryNames
{
    /// <summary>
    /// ActivitySource and Meter name for AI client calls (MedGemma). One name covers
    /// both signals: ApmExtensions passes it to AddSource and AddMeter.
    /// </summary>
    public const string AiSource = "CardiTrack.Ai";
}
