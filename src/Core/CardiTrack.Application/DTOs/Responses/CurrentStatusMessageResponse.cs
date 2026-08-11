namespace CardiTrack.Application.DTOs.Responses;

/// <summary>
/// A short, empathetic, MedGemma-generated line describing a CardiMember's current status —
/// what the Dashboard's hero card shows once it resolves, replacing the fixed per-severity-tier
/// copy the client already renders while this is in flight or unavailable.
/// </summary>
public class CurrentStatusMessageResponse
{
    /// <summary>
    /// Null when there's nothing to say yet — an unknown or paused status has no signal to
    /// interpret, so the client keeps its existing static copy rather than showing an error.
    /// </summary>
    public string? Message { get; init; }

    public required DateTimeOffset GeneratedAt { get; init; }
}
