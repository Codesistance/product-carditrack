namespace CardiTrack.Worker;

public class InactivityDetectionOptions
{
    /// <summary>
    /// Minutes without any granular reading before a device counts as silent — 2 hours per
    /// docs/llm_design.md: long enough to survive a shower and a sync hiccup, short enough
    /// that a morning fall is noticed before lunch.
    /// </summary>
    public int SilenceThresholdMinutes { get; set; } = 120;

    /// <summary>Local hour (inclusive) from which silence starts to matter (llm_design: 07:00).</summary>
    public int WakingStartHour { get; set; } = 7;

    /// <summary>Local hour (exclusive) after which silence stops mattering (llm_design: 22:00).</summary>
    public int WakingEndHour { get; set; } = 22;
}
