namespace CardiTrack.Application.DTOs.Responses;

public class NotificationPreferenceResponse
{
    public TimeOnly? QuietHoursStart { get; set; }
    public TimeOnly? QuietHoursEnd { get; set; }
    public bool ShowDetailsOnLockScreen { get; set; }

    /// <summary>
    /// Whether an alert escalated to this person — one nobody else answered — may wake them
    /// inside their own quiet hours. False unless they said otherwise.
    /// </summary>
    public bool EscalatedAlertsPierceQuietHours { get; set; }
    public List<string> MutedCategories { get; set; } = [];
}
