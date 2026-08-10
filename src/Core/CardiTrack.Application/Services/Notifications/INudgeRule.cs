namespace CardiTrack.Application.Services.Notifications;

/// <summary>
/// One detectable gap between what CardiTrack needs and what the account has supplied.
/// </summary>
/// <remarks>
/// Implementations must be <b>pure</b>: no I/O, no <c>DateTime.UtcNow</c>, no repositories. Every
/// input arrives on <see cref="NudgeContext"/>, which makes each rule a table-driven unit test and
/// keeps the Application layer free of package references.
/// </remarks>
public interface INudgeRule
{
    /// <summary>Stable catalogue key. Persisted, so renaming one is a data migration.</summary>
    string RuleCode { get; }

    /// <summary>
    /// Increment when detection or copy changes materially. A muted rule re-arms only on an
    /// increase — we may ask again when the offer has changed, not merely because time passed.
    /// </summary>
    int Version { get; }

    NudgeSpec Spec { get; }

    NudgeVerdict Evaluate(NudgeContext context);
}
