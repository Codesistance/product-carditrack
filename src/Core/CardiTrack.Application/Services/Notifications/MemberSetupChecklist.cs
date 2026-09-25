using CardiTrack.Application.Services.Notifications.Rules;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Services.Notifications;

/// <summary>
/// The per-member setup checklist behind the dashboard's "Complete the picture" progress ring:
/// which of the setup nudges apply to a member, and which of those are done.
/// </summary>
/// <remarks>
/// <para>
/// <b>Derived from the nudge rules, never restated.</b> Each step is one or more
/// <see cref="ISetupStepRule"/>s taken from <see cref="NudgeRuleCatalogue.All"/>, and whether it is
/// done is that rule's own <see cref="ISetupStepRule.CheckSetup"/> — the predicate its
/// <see cref="INudgeRule.Evaluate"/> is built on. A step's priority is its rules' priority, and a
/// mute is the reconciler's own <see cref="NudgeReconciler.IsMuted"/>.
/// </para>
/// <para>
/// <b>Done is the data, not the inbox.</b> A stored notification row is the wrong source: rows
/// are raised on a cadence, capped at three new per run, held back through the 48-hour new-account
/// grace, a member's pause and an open red alert, and withdrawn while a member is paused. A ring
/// read off rows would tick to "5 of 5" the moment a red alert opened. The timing gates decide
/// when a caregiver is <em>asked</em>; they say nothing about whether the thing is there. Snoozing
/// changes nothing here, then, by construction: a snoozed gap is still a gap.
/// </para>
/// <para>
/// Pure, like the rules: the context is the only input.
/// </para>
/// </remarks>
public static class MemberSetupChecklist
{
    /// <summary>One checklist step: a stable key, a short label, and the rules that decide it.</summary>
    /// <param name="Key">Stable wire identifier — clients key copy and analytics off it, so renaming one is a breaking change.</param>
    /// <param name="Title">A short English label, for a client with no copy of its own for <paramref name="Key"/>.</param>
    /// <param name="Rules">
    /// Usually one. Several when the rules split one thing a caregiver does into more than one
    /// question — medical information is asked for once (empty) and re-confirmed later (stale).
    /// </param>
    public sealed record Step(string Key, string Title, IReadOnlyList<ISetupStepRule> Rules)
    {
        /// <summary>The most urgent of its rules' priorities — the order the nudges themselves use.</summary>
        public NotificationPriority Priority { get; } = Rules.Min(r => r.Spec.Priority);
    }

    /// <summary>One step's answer for one member.</summary>
    public sealed record StepResult(Step Step, bool Done, string ActionDeepLink);

    /// <summary>
    /// Every step, in the order the checklist reads: by priority, as the nudges rank, and within a
    /// priority in the order declared here — reaching help, then the data the device sends, then
    /// the clock, then the background.
    /// </summary>
    public static IReadOnlyList<Step> Steps { get; } =
    [
        .. new Step[]
        {
            new("emergency-contact", "Emergency contact", [Rule<EmergencyContactMissingRule>()]),
            new("sleep-access", "Sleep access", [Rule<SleepScopeMissingRule>()]),
            new("irregular-rhythm", "Irregular rhythm notifications", [Rule<IrnNotEnrolledRule>()]),
            new("time-zone", "Time zone", [Rule<TimezoneDefaultRule>()]),
            new("medical-information", "Medical information",
                [Rule<MedicalNotesEmptyRule>(), Rule<MedicalNotesStaleRule>()])
        }.OrderBy(s => s.Priority) // OrderBy is stable: ties keep the declared order.
    ];

    /// <summary>
    /// The applicable steps for one member context, in <see cref="Steps"/> order. A step is left
    /// out when none of its rules applies, or when every rule that does is muted by the context's
    /// user — "don't ask again" takes the step off their total rather than leaving it open forever.
    /// </summary>
    /// <remarks>
    /// Within a step, a muted rule simply drops out and the rest decide: a caregiver who muted
    /// "add a health background" has not muted "is it still right?", and if notes later appear
    /// and go stale the step returns. The step is done only when every remaining rule that
    /// applies is done.
    /// </remarks>
    /// <exception cref="ArgumentException">The context has no member — the checklist is per member.</exception>
    public static IReadOnlyList<StepResult> Evaluate(NudgeContext context)
    {
        if (context.Member is null)
            throw new ArgumentException("The setup checklist is evaluated per member.", nameof(context));

        var results = new List<StepResult>(Steps.Count);

        foreach (var step in Steps)
        {
            var checks = step.Rules
                .Where(rule => !NudgeReconciler.IsMuted(context, rule))
                .Select(rule => rule.CheckSetup(context))
                .Where(check => check.State != SetupStepState.NotApplicable)
                .ToList();

            if (checks.Count == 0)
                continue;

            // The link that closes the first open question, or — all done — the first rule's, so
            // a finished step still opens the screen that holds what was entered.
            var firstOpen = checks.FindIndex(c => c.IsNotDone);
            var done = firstOpen < 0;

            results.Add(new StepResult(step, done, checks[done ? 0 : firstOpen].ActionDeepLink));
        }

        return results;
    }

    private static ISetupStepRule Rule<TRule>() where TRule : ISetupStepRule =>
        NudgeRuleCatalogue.All.OfType<TRule>().Single();
}
