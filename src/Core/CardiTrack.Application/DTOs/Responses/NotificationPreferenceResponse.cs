namespace CardiTrack.Application.DTOs.Responses;

public class NotificationPreferenceResponse
{
    public TimeOnly? QuietHoursStart { get; set; }
    public TimeOnly? QuietHoursEnd { get; set; }
    public bool ShowDetailsOnLockScreen { get; set; }
    public List<string> MutedCategories { get; set; } = [];
}
