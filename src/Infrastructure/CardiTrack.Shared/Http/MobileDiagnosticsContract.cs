namespace CardiTrack.Shared.Http;

/// <summary>
/// The wire contract between the mobile app's error-log relay and the API endpoint that
/// receives it. Lives here for the same reason <see cref="ClientHeaderNames"/> does: the
/// sender (CardiTrack.Mobile.Core) and the reader (CardiTrack.API) share no other assembly,
/// and every ceiling below is enforced on both sides — the app truncates before it queues,
/// the API refuses anything over the same limit — so they must be one number, not two.
/// </summary>
/// <remarks>
/// Why a relay exists at all: the mobile Datadog SDK cannot ship to this org's UK1 site (see
/// docs/technical/apm_setup_runbook.md §5), so the only way an unhandled exception on a
/// caregiver's phone reaches Datadog is through our own API, which already ships there. The
/// API re-emits each entry as <see cref="ServiceName"/> rather than as itself.
/// </remarks>
public static class MobileDiagnosticsContract
{
    /// <summary>
    /// Header carrying the shared key. Not <c>Authorization</c>: this is not a bearer credential
    /// for a user, and nothing that harvests bearer tokens should pick it up.
    /// </summary>
    public const string KeyHeader = "X-Mobile-Diagnostics-Key";

    /// <summary>Relative to the API base URL, as the app's other paths are.</summary>
    public const string LogsPath = "api/v1/mobile/diagnostics/logs";

    /// <summary>
    /// The Datadog service the relayed entries land under. The same string
    /// <c>MobileApm</c> gives the (inert) on-device SDK, so a future direct route and this
    /// relay would meet in one place.
    /// </summary>
    public const string ServiceName = "carditrack-mobile";

    public const int MaxEntriesPerBatch = 50;
    public const int MaxMessageLength = 4_000;

    /// <summary>The whole exception chain as text — type, message, stack and every inner exception.</summary>
    public const int MaxExceptionLength = 64_000;

    /// <summary>Structured frames across the exception chain, outermost exception first.</summary>
    public const int MaxFrames = 128;

    /// <summary>Method, declaring type, assembly and file name on a frame.</summary>
    public const int MaxFrameFieldLength = 512;

    /// <summary>
    /// The tail of the device's own Warning+ log that a crash entry carries — what the app was
    /// doing in the minutes before it died. Kept from the end, so the newest lines survive.
    /// </summary>
    public const int MaxRecentLogLength = 16_000;

    /// <summary>Platform, versions, handset, locale, install id, screen, thread and source context.</summary>
    public const int MaxFieldLength = 200;

    /// <summary>
    /// Request body ceiling. Generous against the per-entry limits (fifty entries at their
    /// maximum would not fit, and never need to — one crash is one entry), tight against abuse.
    /// </summary>
    public const int MaxBodyBytes = 524_288;
}
