using System.ComponentModel.DataAnnotations;

namespace CardiTrack.Domain.Enums;

/// <summary>
/// The split that drives delivery behaviour: whether it pushes, whether quiet hours apply,
/// whether it escalates, whether it may be silenced (notification_engine.md §3).
/// </summary>
/// <remarks>
/// Deliberately distinct from <see cref="NotificationCategory"/>, which sub-categorises
/// <see cref="Entities.Notification"/> rows only (Safety/Blocking/Unlock/Account, for nudge
/// silencing rules) and has no meaning for an <see cref="Entities.Alert"/>. A
/// <see cref="Entities.NotificationDelivery"/> row is polymorphic over three sources, so its
/// category has to mean something for each: a Safety-class <see cref="NotificationCategory"/>
/// notification maps to <see cref="Safety"/>; every <see cref="Entities.Alert"/> maps to
/// <see cref="Health"/> regardless of <see cref="AlertSeverity"/> (severity drives push-vs-in-app
/// and quiet-hours override within Health, not the category itself); nudge-shaped notifications map
/// to <see cref="Nudge"/>; every <see cref="Entities.MemberQuestionnaire"/> maps to
/// <see cref="Questionnaire"/>.
/// </remarks>
public enum DeliveryCategory
{
    /// <summary>Monitoring is down, or nobody is listening. Pushes immediately, overrides quiet hours, escalates.</summary>
    [Display(Name = "Safety")]
    Safety = 1,

    /// <summary>A red/orange anomaly in the wearer's data. Red overrides quiet hours and escalates; orange defers and does not.</summary>
    [Display(Name = "Health")]
    Health = 2,

    /// <summary>A data gap the user can close. In-app only except the two safety-class nudge rules; never escalates.</summary>
    [Display(Name = "Nudge")]
    Nudge = 3,

    /// <summary>A question the service is asking the family. Pushes, deferred (not overridden) by
    /// quiet hours like orange Health; never escalates — QuestionnaireAlertWorker's own reminder
    /// cadence is what re-alerts an unanswered one, not the escalation ladder.</summary>
    [Display(Name = "Questionnaire")]
    Questionnaire = 4,

    /// <summary>
    /// Good news: nothing has been raised about a member for long enough that the silence itself
    /// is worth reporting. The only category whose whole point is that nothing is wrong, which is
    /// exactly why it is a category rather than a quiet <see cref="Health"/> row — it must never
    /// borrow Health's escalation, its alert chime, or its power to override quiet hours. It
    /// pushes, defers to quiet hours in full, and never escalates.
    /// </summary>
    [Display(Name = "Reassurance")]
    Reassurance = 5
}
