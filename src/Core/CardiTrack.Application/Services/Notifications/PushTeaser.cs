using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Services.Notifications;

/// <summary>
/// Lock-screen push copy that stays free of PHI (no names, no metrics) while still varying by
/// what happened — and always asks the caregiver to open the app (notification_engine.md §7.1).
/// </summary>
public static class PushTeaser
{
    public const string AppName = "CardiTrack";

    public static (string Title, string Body) For(
        DeliveryCategory category,
        AlertSeverity? severity = null,
        AlertType? alertType = null) =>
        category switch
        {
            DeliveryCategory.Safety => (AppName, "Urgent — open CardiTrack now"),
            DeliveryCategory.Health => Health(severity, alertType),
            _ => (AppName, "Something needs your attention — open CardiTrack"),
        };

    private static (string Title, string Body) Health(AlertSeverity? severity, AlertType? alertType)
    {
        var title = alertType switch
        {
            AlertType.HeartRate => "Heart rate alert",
            AlertType.Sleep => "Sleep alert",
            AlertType.Inactivity => "Activity alert",
            AlertType.PatternBreak => "Pattern alert",
            AlertType.Trend => "Trend alert",
            _ => "Health alert",
        };

        var body = severity == AlertSeverity.Red
            ? "Urgent — open CardiTrack to check."
            : "Open CardiTrack to check on this.";

        return (title, body);
    }
}
