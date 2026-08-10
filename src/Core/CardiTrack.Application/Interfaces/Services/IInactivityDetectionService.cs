namespace CardiTrack.Application.Interfaces.Services;

/// <summary>
/// When a device's silence becomes an alert. A named record rather than loose parameters: the
/// three values only mean anything together, and the worker forwards them from configuration
/// in one piece.
/// </summary>
public sealed record InactivityDetectionRules
{
    /// <summary>Minutes without any granular reading before a device counts as silent.</summary>
    public required int SilenceThresholdMinutes { get; init; }

    /// <summary>Local hour (inclusive) from which silence starts to matter.</summary>
    public required int WakingStartHour { get; init; }

    /// <summary>Local hour (exclusive) after which silence stops mattering.</summary>
    public required int WakingEndHour { get; init; }
}

/// <summary>
/// One pass of rule-based device-silence detection: for every recently-active member whose
/// device has produced no granular readings for the configured threshold during their waking
/// hours, raise a yellow device-check alert. No AI involvement — this is the failsafe *under*
/// the AI pipeline, covering exactly the case every generated artifact deliberately skips:
/// silence. A dead battery must produce something, and this is the something.
/// </summary>
public interface IInactivityDetectionService
{
    /// <summary>Checks every candidate member once; returns how many alerts were raised.</summary>
    Task<int> DetectAsync(DateTime utcNow, InactivityDetectionRules rules, CancellationToken ct = default);
}
