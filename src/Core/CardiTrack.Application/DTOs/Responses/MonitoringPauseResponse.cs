namespace CardiTrack.Application.DTOs.Responses;

/// <summary>Pause state after a pause or resume call (M1-13).</summary>
public class MonitoringPauseResponse
{
    public bool MonitoringPaused { get; set; }
    public DateTime? MonitoringPausedUntil { get; set; }
    public string? MonitoringPauseReason { get; set; }
}
