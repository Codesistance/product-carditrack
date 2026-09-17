namespace CardiTrack.Application.Interfaces.Security;

/// <summary>
/// The shared key in front of the anonymous mobile crash-log relay. Implemented in
/// Infrastructure; declared here so the API controller does not take a concrete
/// Infrastructure type.
/// </summary>
public interface IMobileDiagnosticsKey
{
    /// <summary>False when the environment has no usable key — the endpoint must 404.</summary>
    bool IsConfigured { get; }

    /// <summary>Constant-time compare of the presented header against the configured value.</summary>
    bool Matches(string? presented);
}
