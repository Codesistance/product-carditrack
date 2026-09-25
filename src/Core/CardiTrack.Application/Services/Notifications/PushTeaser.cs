using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Services.Notifications;

/// <summary>
/// Lock-screen push copy that stays free of PHI (no names, no metrics) while still varying by
/// what happened — and always asks the caregiver to open the app (notification_engine.md §7.1).
/// </summary>
public static class PushTeaser
{
    public const string AppName = "CardiTrack";

    /// <param name="nudgeRuleCode">
    /// The rule behind a nudge-sourced push (<c>NotificationDelivery.NudgeRuleCode</c>). Only the
    /// rules whose generic teaser would mislead get their own; the rest keep the category's.
    /// </param>
    public static (string Title, string Body) For(
        DeliveryCategory category,
        AlertSeverity? severity = null,
        AlertType? alertType = null,
        string? nudgeRuleCode = null) =>
        category switch
        {
            // A refused grant is Safety class because monitoring is down, but "Urgent" oversells
            // it and says nothing about the fix: nobody needs checking on, a device needs signing
            // in again. Still PHI-free — no name and no device, just the kind of problem.
            DeliveryCategory.Safety when nudgeRuleCode == Rules.DeviceAuthBrokenRule.Code =>
                ("Device needs reconnecting", "Open CardiTrack to sign it in again so readings keep coming through."),
            DeliveryCategory.Safety => (AppName, "Urgent — open CardiTrack now"),
            DeliveryCategory.Health => Health(severity, alertType),
            DeliveryCategory.Questionnaire => (AppName, "A question is waiting — open CardiTrack"),
            // The one teaser a caregiver is allowed to read and then put the phone down. Every
            // other line here ends in "open CardiTrack" because the detail is the point; here the
            // whole message *is* the lock screen, and demanding a tap to find out that nothing is
            // wrong would make the good news cost the same as the bad. The invitation stays, but
            // as an offer rather than an instruction.
            DeliveryCategory.Reassurance => ("All quiet", "Nothing has come up this week. Open CardiTrack for the details."),
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
