namespace CardiTrack.Worker;

/// <summary>
/// Tunables for <see cref="Workers.HistoryRepullWorker"/>, bound from its own
/// <c>Workers:HistoryRepullWorker</c> section alongside the cron.
/// </summary>
public class HistoryRepullOptions
{
    /// <summary>
    /// Open re-pull requests advanced per tick — one chunk each. Bounds a tick's provider spend:
    /// a 7-day chunk is up to 182 requests at one page per series (up to 26 a day: the snapshot and
    /// the five granular series; a high-cadence series adds a request per extra page) against one
    /// wearer's 300/min ceiling, and each request is a different wearer, so five in a tick is five
    /// wearers each well inside their own limit.
    /// </summary>
    public int MaxPerTick { get; set; } = 5;
}
