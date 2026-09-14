using CardiTrack.Application.DTOs.Requests;

namespace CardiTrack.Mobile.Core.Diagnostics;

/// <summary>
/// Gets the app's Error-and-above log lines off the phone and into Datadog by way of the API
/// (<c>POST api/v1/mobile/diagnostics/logs</c>), because the mobile Datadog SDK cannot ship to
/// this org's site at all. Entries are queued on disk and sent in batches, so a line written in
/// the last moments of a crashing process is still delivered on the next launch.
/// </summary>
public interface IMobileDiagnosticsRelay
{
    /// <summary>False when the build carries no key or no API address — every call is then a no-op.</summary>
    bool Enabled { get; }

    /// <summary>Queues one entry. Never throws: this is called from inside the logger.</summary>
    void Record(MobileDiagnosticsLogEntry entry);

    /// <summary>
    /// Sends what is queued. True when the queue is empty afterwards; false when it is not
    /// (offline, refused, or another flush already running) — in which case the entries wait.
    /// </summary>
    Task<bool> FlushAsync(CancellationToken ct = default);

    /// <summary>
    /// The crash path: blocks the calling thread for at most <paramref name="budget"/> while a
    /// flush runs, because the process is about to be aborted and there will be no "later" in
    /// this run. Whatever does not make it stays queued for the next launch.
    /// </summary>
    bool TryFlushBlocking(TimeSpan budget);
}
