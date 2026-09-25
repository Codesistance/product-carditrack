using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Services;

/// <summary>
/// Readings rendered the way a person says them. Split from the prompt blocks in
/// <c>Infrastructure/Services/MedicalPromptBlocks</c> because these renderings are not
/// prompt-only: the code-assembled replies in <see cref="MemberChatReplies"/> speak them straight
/// to a caregiver, and reply composition lives here, where it is testable without a host.
/// </summary>
public static class ReadingFigures
{
    /// <summary>
    /// A night's sleep as a person says it, or "not measured" when the night is missing. The
    /// minutes are how the wearable stores it and how every table holds it, and sending that
    /// number to a model got it repeated back verbatim: a caregiver asking how their father slept
    /// was told "372 minutes", which is arithmetic homework in the middle of a sentence meant to
    /// reassure. Nobody has ever asked how many minutes someone slept.
    /// </summary>
    /// <remarks>
    /// Rounded to the minute rather than to the nearest quarter-hour. "6h 12m" is no harder to
    /// read than "about 6¼ hours" and stays true to the reading, which matters when the same
    /// figure appears on a chart's axis beside it. Under an hour keeps minutes alone — "0h 40m"
    /// is a worse way of writing forty minutes.
    /// </remarks>
    public static string SleepFigure(int? minutes) => minutes switch
    {
        null => "not measured",
        < 60 => $"{minutes}m",
        _ => minutes % 60 == 0 ? $"{minutes / 60}h" : $"{minutes / 60}h {minutes % 60}m",
    };

    /// <summary>What an awake night is called wherever a night is named.</summary>
    public const string AwakeNight = "awake all night (watch worn, no sleep recorded)";

    /// <summary>What a night still waiting on its morning sync is called.</summary>
    public const string PendingNight = "not arrived yet";

    /// <summary>
    /// A night as a person says it, knowing what is known about it: <see cref="AwakeNight"/> for a
    /// night the watch was worn through with no sleep, <see cref="PendingNight"/> for one that may
    /// still arrive, and otherwise <see cref="SleepFigure"/>.
    /// </summary>
    /// <remarks>
    /// An awake night is stored as 0 minutes so every average counts it, and "0m" is the one
    /// rendering of it that must never reach a reader: it reads as a figure missing its digits,
    /// or as a watch that measured nothing — the opposite of what the status establishes
    /// (decision 2026-09-25: worn with no sleep is awake, "no ambiguity").
    /// </remarks>
    public static string NightFigure(int? minutes, NightSleepStatus? status) => status switch
    {
        NightSleepStatus.Awake => AwakeNight,
        NightSleepStatus.Pending when minutes is null => PendingNight,
        _ => SleepFigure(minutes),
    };
}
