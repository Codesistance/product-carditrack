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

    /// <summary>Everything a member-context source could contribute to.</summary>
    All = Digest | RealtimeAssessment | AlertInsight | BaselineInsight | CurrentStatus | Daybook
        | Weekbook | Monthbook,
}
