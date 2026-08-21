namespace CardiTrack.Infrastructure.Services.PromptContext;

/// <summary>
/// Which generated text a member-context section is being built for. Every prompt this platform
/// sends the private model names one, and every <see cref="IMemberContextSource"/> declares the
/// set it contributes to — so what a source knows reaches the prompts it is useful to and stays
/// out of the ones it would only crowd.
/// </summary>
/// <remarks>
/// Flags rather than an enum because the interesting statement a source makes is "these three,
/// not that one" — <c>Digest | RealtimeAssessment</c> reads as the sentence it is.
/// </remarks>
[Flags]
public enum PromptPurpose
{
    /// <summary>The family summary written by the digest job.</summary>
    Digest = 1,

    /// <summary>The hourly severity assessment written by the assessor job.</summary>
    RealtimeAssessment = 2,

    /// <summary>The on-demand explanation of one alert.</summary>
    AlertInsight = 4,

    /// <summary>The on-demand baseline/trend analysis, in all three of its baseline states.</summary>
    BaselineInsight = 8,

    /// <summary>The dashboard's ambient hero line.</summary>
    CurrentStatus = 16,

    /// <summary>
    /// The account of one finished day, written after the member's local day is over. Distinct
    /// from <see cref="Digest"/> in tense and in register — that one describes a day still
    /// running and is rewritten as it does.
    /// </summary>
    Daybook = 32,

    /// <summary>
    /// The account of one finished week. Shares the Daybook's register and tense; differs in
    /// altitude — trajectory across seven days rather than completeness within one.
    /// </summary>
    Weekbook = 64,

    /// <summary>
    /// The account of one finished calendar month. Shares the register and tense of the books
    /// below it; differs in altitude again — the month's shape rather than a week's trajectory.
    /// </summary>
    Monthbook = 128,

    /// <summary>
    /// One turn of the persisted, multi-turn caregiver chat about a member. Picks 256 rather than
    /// the next sequential bit deliberately: an unmerged branch (PR #339, "Let caregivers ask
    /// MedGemma about a CardiMember") independently defines a one-shot <c>MemberAsk</c> purpose at
    /// 256 for a different, non-persisted feature. If that branch merges first, resolve the clash by
    /// moving whichever purpose lands second to the next free bit — do not let the two share a
    /// value, since the whole point of <c>[Flags]</c> here is that each purpose is independently
    /// addressable.
    /// </summary>
    MemberChat = 256,

    /// <summary>
    /// The on-demand wellness suggestion: one action grounded in the member's own readings and a
    /// fixed set of public-health wellness references (activity, sleep, resting heart rate — see
    /// <c>MedicalPromptBlocks.WellnessGuidelineReference</c>), phrased as something worth
    /// mentioning to a clinician rather than a diagnosis or a treatment change. CardiTrack is not
    /// a medical device (docs/solution_manifest.md), so this purpose is deliberately grounded in
    /// general wellness guidance rather than clinical/diagnostic guidelines.
    /// </summary>
    Advise = 512,

    /// <summary>Everything a member-context source could contribute to.</summary>
    All = Digest | RealtimeAssessment | AlertInsight | BaselineInsight | CurrentStatus | Daybook
        | Weekbook | Monthbook | MemberChat | Advise,
}
