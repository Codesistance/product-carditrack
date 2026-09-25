namespace CardiTrack.Mobile.Core.Alerts;

/// <summary>
/// An alert severity's word and colour, the same wherever an alert is drawn — the detail screen's
/// badge and banner, the respond screen's context card.
/// </summary>
/// <remarks>
/// NOTICE, not INFO, for yellow: "INFO" was doing duty for green as well, which flattened "nothing
/// to report" and "something is different" into one word, and on an amber banner the mildest word
/// in the vocabulary read as a contradiction. Green keeps its own colour rather than the
/// unknown-severity grey: the AI assessor grades its mildest findings green, and older sleep alerts
/// are still on file. Diverges from M1-10's CRITICAL/URGENT/INFO wording on purpose.
/// </remarks>
public static class AlertSeverityLook
{
    /// <returns>The badge word, and the colour resource key its ink and rail take.</returns>
    public static (string Word, string ColorKey) For(string? severity) => severity switch
    {
        "red" => ("CRITICAL", "StatusRed"),
        "orange" => ("URGENT", "StatusOrange"),
        "yellow" => ("NOTICE", "StatusYellow"),
        "green" => ("INFO", "StatusGreen"),
        _ => ("INFO", "StatusUnknown"),
    };
}
