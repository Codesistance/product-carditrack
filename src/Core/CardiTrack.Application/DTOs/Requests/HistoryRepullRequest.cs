namespace CardiTrack.Application.DTOs.Requests;

/// <summary>
/// Body of <c>POST /api/v1/cardimembers/{id}/devices/{deviceId}/history-repull</c>: how many
/// complete days back from yesterday to re-read. 1–90; the mobile app offers a fixed set of
/// presets, but the API accepts any value in range.
/// </summary>
public class HistoryRepullRequest
{
    public int Days { get; set; }
}
