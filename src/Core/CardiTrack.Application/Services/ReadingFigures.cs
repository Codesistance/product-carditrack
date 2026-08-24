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
}
