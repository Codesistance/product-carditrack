using CardiTrack.Mobile.Core.Auth;

namespace CardiTrack.Mobile.Core.Chat;

/// <summary>
/// The one-time notice that the member chat is answered by an AI system — EU AI Act Art. 50(1):
/// a person interacting with an AI system is told so, at the latest at the first interaction
/// (#1244). Shown the first time a caregiver opens the chat, and again ahead of a send if it has
/// somehow not been seen; its only answer is "Got it". Fixed copy, never model output, worded as
/// the PDF report and the transcript export already put it.
/// </summary>
/// <remarks>
/// <para>
/// This replaced a line under the chat header that said the same thing on every screen of the
/// sheet. It met the obligation but crowded a header built for a title, one subtitle and two
/// buttons; the owner asked for a one-time confirmation instead (2026-09-25). The exports keep
/// their own wording, since a document travels without the app that showed the notice.
/// </para>
/// <para>
/// "Seen" is remembered per caregiver, keyed by the same one-way token as the telemetry notice
/// (<see cref="HealthDataDisclosureScope"/>): a phone that changes hands shows it again to the
/// next person rather than letting one caregiver's acknowledgement stand for theirs.
/// </para>
/// </remarks>
public static class AiChatNotice
{
    public const string Title = "About this chat";

    public const string Message =
        "Answers here are written by CardiTrack's AI assistant, not a clinician. It reads the "
        + "readings on file to answer, and it can get things wrong — for anything about their "
        + "health or care, their doctor is the one to ask.";

    public const string AcknowledgeText = "Got it";

    /// <summary>
    /// The value to store once the caregiver has seen the notice, or null when there is no
    /// signed-in identity to tie it to — in which case nothing is remembered and it shows again.
    /// </summary>
    public static string? SeenValueFor(string? email) => HealthDataDisclosureScope.For(email);

    /// <summary>Whether <paramref name="stored"/> records that this caregiver has seen it.</summary>
    public static bool IsSeen(string? stored, string? email) =>
        SeenValueFor(email) is { } scope && string.Equals(stored, scope, StringComparison.Ordinal);
}
