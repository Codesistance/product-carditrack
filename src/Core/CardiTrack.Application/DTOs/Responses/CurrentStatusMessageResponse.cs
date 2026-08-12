namespace CardiTrack.Application.DTOs.Responses;

/// <summary>
/// A short, empathetic, MedGemma-generated read on a CardiMember's current state — what the
/// Dashboard's hero card shows once it resolves, replacing the fixed per-severity-tier copy the
/// client already renders while this is in flight or unavailable.
/// </summary>
public class CurrentStatusMessageResponse
{
    /// <summary>
    /// The punchy note: a few words a caregiver takes in at a glance ("All steady", "Quieter than
    /// usual"). Null when the model gave nothing usable — the client keeps its per-tier headline
    /// rather than leaving the row headless, so this can be absent while
    /// <see cref="Message"/> is not.
    /// </summary>
    public string? Headline { get; init; }

    /// <summary>
    /// The sentence under the headline. Null when there's nothing to say yet — an unknown or
    /// paused status has no signal to interpret, so the client keeps its existing static copy
    /// rather than showing an error.
    /// </summary>
    public string? Message { get; init; }

    public required DateTimeOffset GeneratedAt { get; init; }
}
