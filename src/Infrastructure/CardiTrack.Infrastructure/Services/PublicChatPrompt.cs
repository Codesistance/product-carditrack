using CardiTrack.Application.DTOs.Common;
using CardiTrack.Domain.Entities;

namespace CardiTrack.Infrastructure.Services;

/// <summary>
/// The context turn the general-provider chat endpoint opens with: a brief, and the member's last
/// three days of readings.
/// </summary>
/// <remarks>
/// <para>
/// Built here rather than in the controller that sends it. That is not tidying: this was the one
/// prompt on the platform that lived in the presentation layer, and it was also the one with no
/// tone block, no register, no injection guardrail and no test coverage — because it sat nowhere
/// near the code that defines any of them. <c>MedicalPromptToneTests</c> reflects over services in
/// this assembly, so a prompt outside it is a prompt outside every rule the suite enforces.
/// </para>
/// <para>
/// Carries readings only. This path goes to the general provider — today Gemini's consumer
/// endpoint, outside the Google Cloud BAA — so nothing that identifies the member goes with them.
/// The CardiMember id used to be included and bought the model nothing: it cannot look the id up,
/// and the caller already knows whose data they asked for.
/// </para>
/// </remarks>
public static class PublicChatPrompt
{
    /// <summary>
    /// The brief. Was one sentence — "You are a helpful health assistant; answer questions about
    /// this data accurately and concisely" — with the readings inlined behind a "[System context]"
    /// prefix.
    /// </summary>
    /// <remarks>
    /// <para>
    /// That prefix is not a role. The message is sent as <see cref="ChatRole.User"/>, so a
    /// caregiver who types "[System context]" produces text the model cannot tell from this block.
    /// Naming the readings as a section instead is the same delimiter every other prompt uses, and
    /// the guardrail below says what to do with the turn that follows.
    /// </para>
    /// <para>
    /// "Helpful health assistant" was also the only brief on the platform with no rule about what
    /// it may claim — no diagnosis ban, nothing about adding urgency or reassurance the readings do
    /// not carry — on the one path that reaches an off-estate provider.
    /// </para>
    /// </remarks>
    internal const string Instructions =
        MedicalPromptBlocks.Tone + MedicalPromptBlocks.CaregiverRegister + """

        The readings below are a CardiMember's, over their last three days. Answer the caregiver's
        questions about them accurately and concisely. Do not quote a figure that is not below, and
        where a reading was not measured, say so rather than letting it read as an ordinary one.
        """ + MedicalPromptBlocks.ChatMessageGuardrail;

    /// <summary>The section heading the readings sit under.</summary>
    internal const string ReadingsLabel = "Readings, last 3 days";

    /// <summary>The context turn, ready to prepend to the caller's history.</summary>
    public static ChatMessage BuildContextMessage(IEnumerable<ActivityLog> logs)
    {
        var rows = logs.OrderBy(l => l.Date).Select(l => $"{l.Date}: {DayFigures(l)}").ToList();
        var summary = rows.Count > 0
            ? string.Join(", ", rows)
            : "No recent activity data available.";

        return new ChatMessage
        {
            Role = ChatRole.User,
            Content = $"{Instructions}\n\n--- {ReadingsLabel} ---\n{summary}",
        };
    }

    /// <summary>
    /// One day's figures, naming only what the device reported.
    /// </summary>
    /// <remarks>
    /// The three readings were interpolated straight into the line, so a day the watch missed one
    /// rendered "steps=, HR=71, sleep=min" — an empty value beside a real one. The family digest
    /// guards this and asserts on it; this was the fourth renderer in the prompt review that did
    /// not. The sleep key says which night it is for the reason the digest spells out: a summary
    /// once called a member's poor night good because the model could not tell which night a row's
    /// sleep figure belonged to.
    /// </remarks>
    private static string DayFigures(ActivityLog log)
    {
        var parts = new List<string>(3);
        if (log.Steps is { } steps)
            parts.Add($"steps={steps}");
        if (log.RestingHeartRate is { } resting)
            parts.Add($"HR={resting}");
        if (log.SleepMinutes is { } sleep)
            parts.Add($"sleep(night ending that morning)={sleep}min");

        return parts.Count > 0 ? string.Join(", ", parts) : "nothing measured";
    }
}
