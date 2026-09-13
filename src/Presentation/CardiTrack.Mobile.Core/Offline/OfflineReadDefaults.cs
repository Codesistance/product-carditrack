namespace CardiTrack.Mobile.Core.Offline;

/// <summary>
/// The unfiltered questions every live screen asks on a cold landing — and therefore the
/// questions a push-triggered warm must answer so those landings paint from the device.
/// </summary>
public static class OfflineReadDefaults
{
    /// <summary>
    /// How many journal entries one load asks for. A month is a page a caregiver actually
    /// scrolls; the service clamps anything larger.
    /// </summary>
    public const int JournalHistoryLimit = 31;

    public const int QuestionnairePage = 1;
    public const int QuestionnairePageSize = 20;

    /// <summary>
    /// The Alerts tab's default filter — open, not resolved. The warmer must ask this same
    /// question so the tab's peek hits the snapshot a push just wrote.
    /// </summary>
    public const string OpenAlertStatus = "open";

    /// <summary>
    /// Alert-detail fetches after the list lands. The list itself is what the Alerts tab
    /// shows; twenty details cover a typical open inbox without turning a wake into a storm.
    /// </summary>
    public const int AlertDetailLimit = 20;

    public const int MaxConcurrency = 4;

    /// <summary>
    /// Ceiling on one warm. A push must never keep a backgrounded process working past this;
    /// anything still in flight is retried the next time the app is opened.
    /// </summary>
    public static readonly TimeSpan WarmTimeout = TimeSpan.FromSeconds(45);
}
