namespace CardiTrack.Application.DTOs.Requests;

/// <summary>
/// PUT /api/v1/notifications/preferences. Named distinctly from the deleted
/// <c>NotificationPreferencesRequest</c> (notification_engine.md §14 debt table) — that one
/// described sms/email/push channels this engine never has; this describes quiet hours and
/// lock-screen detail, the actual R2 preference surface.
/// </summary>
public class UpdateNotificationPreferenceRequest
{
    public TimeOnly? QuietHoursStart { get; set; }
    public TimeOnly? QuietHoursEnd { get; set; }

    /// <summary>Opt-in richness (§7.1). A caller that omits this must not silently turn it on — the API default is false.</summary>
    public bool ShowDetailsOnLockScreen { get; set; }

    /// <summary>
    /// Whether an alert escalated to this person may wake them inside their own quiet hours.
    /// Omitting it means no, for the same reason <see cref="ShowDetailsOnLockScreen"/> does: a
    /// caller that forgot the field must not end up waking somebody at 3am on its behalf.
    /// </summary>
    public bool EscalatedAlertsPierceQuietHours { get; set; }

    /// <summary>
    /// Categories the user has muted for push. Safety can never appear here — the server strips
    /// it rather than trusting the client to omit it.
    /// </summary>
    public List<string> MutedCategories { get; set; } = [];
}
