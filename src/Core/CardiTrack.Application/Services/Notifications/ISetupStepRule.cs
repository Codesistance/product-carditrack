namespace CardiTrack.Application.Services.Notifications;

/// <summary>
/// A nudge rule that also backs a step on a member's setup checklist — the "Complete the picture"
/// progress a caregiver sees per CardiMember.
/// </summary>
/// <remarks>
/// <para>
/// A nudge answers <em>"should we ask about this now?"</em>; a checklist step answers <em>"has it
/// been done?"</em>. The two differ only by the rule's timing gates — a day's grace on a new
/// member's emergency contact, the baseline a medical-notes nudge waits for — which decide when
/// asking is welcome, not whether the thing exists. So <see cref="INudgeRule.Evaluate"/> is
/// expected to call <see cref="CheckSetup"/> and add its timing gates on top, which is what keeps
/// the predicate in one place: a step can never read as done while the rule's own test says the
/// gap is there.
/// </para>
/// <para>
/// Pure, like <see cref="INudgeRule.Evaluate"/>: everything arrives on the context.
/// </para>
/// </remarks>
public interface ISetupStepRule : INudgeRule
{
    /// <summary>
    /// Whether what this rule asks for applies to the context and, if so, whether it has been
    /// supplied — ignoring every "not now" gate the nudge applies before it speaks.
    /// </summary>
    SetupCheck CheckSetup(NudgeContext context);
}

/// <summary>Where one setup-checklist rule stands for one context.</summary>
public enum SetupStepState
{
    /// <summary>
    /// The step does not exist for this member — no device that could grant sleep, no device that
    /// can say whether rhythm checks are on. Left out of the total rather than counted as done.
    /// </summary>
    NotApplicable = 0,

    Done = 1,
    NotDone = 2
}

/// <summary>A rule's checklist answer: its state, and the deep link to the screen that closes it.</summary>
/// <param name="State">Whether the step applies and is done.</param>
/// <param name="ActionDeepLink">
/// The same link the rule's nudge carries. Present for done steps as well, so a caregiver can go
/// back and review what they entered; empty only when <paramref name="State"/> is
/// <see cref="SetupStepState.NotApplicable"/>.
/// </param>
public readonly record struct SetupCheck(SetupStepState State, string ActionDeepLink)
{
    public static SetupCheck NotApplicable { get; } = new(SetupStepState.NotApplicable, string.Empty);

    public static SetupCheck Of(bool done, string actionDeepLink) =>
        new(done ? SetupStepState.Done : SetupStepState.NotDone, actionDeepLink);

    public bool IsNotDone => State == SetupStepState.NotDone;
}
