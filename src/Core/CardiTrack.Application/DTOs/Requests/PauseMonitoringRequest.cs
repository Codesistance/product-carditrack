using System.ComponentModel.DataAnnotations;

namespace CardiTrack.Application.DTOs.Requests;

/// <summary>
/// Pauses monitoring for a bounded window (M1-13 "Pause Monitoring"), per the contract in
/// docs/execution/backend/api/cardimembers.md.
/// </summary>
public class PauseMonitoringRequest
{
    /// <summary>
    /// How long to stay paused, 1 hour to 7 days. Bounded on purpose: an open-ended pause
    /// would let someone stop being watched over indefinitely without anyone noticing.
    /// </summary>
    [Range(1, 168, ErrorMessage = "Choose a pause between 1 hour and 7 days")]
    public int DurationHours { get; set; }

    [StringLength(200)]
    public string? Reason { get; set; }
}
