using CardiTrack.Application.DTOs.Common;

namespace CardiTrack.Application.Interfaces.Services;

/// <summary>
/// The settings rung's one model call: reads an alert-settings message into an
/// <see cref="AlertChangePlan"/> — which rule or alarm, and what to do with it — against the closed
/// vocabulary of the rule catalogue, the alarm metric catalogue and the member's existing alarms.
/// It chooses <em>which</em>; the composer and the alert services own <em>what</em> happens.
/// </summary>
public interface IAlertChangePlanner
{
    /// <param name="questionsOnlyHistory">
    /// The caregiver's prior questions, framed — so "turn that one off" after "is his sleep alert
    /// on?" resolves. Questions only, like the router: the model's own prior prose never re-enters
    /// the step that decides what changes.
    /// </param>
    /// <param name="snapshot">
    /// What is currently on for this member. Rendered into the prompt as labels, titles and
    /// composed conditions — never an id, and alarm names already name-redacted.
    /// </param>
    Task<AiGenerationResult<AlertChangePlan>> PlanAsync(
        string question,
        string? questionsOnlyHistory,
        AlertSettingsSnapshot snapshot,
        CancellationToken ct = default);
}
