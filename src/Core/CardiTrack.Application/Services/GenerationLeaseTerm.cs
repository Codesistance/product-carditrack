namespace CardiTrack.Application.Services;

/// <summary>
/// How long a once-per-period generator's claim survives without being released.
/// </summary>
public static class GenerationLeaseTerm
{
    /// <summary>
    /// Twenty minutes, derived from the client's own budget rather than chosen: a single medical
    /// call may run to <c>PrivateAiSettings.TimeoutSeconds</c> — 900 seconds since the pileup
    /// fix (medgemma_serving_architecture.md, MS-8) — and a lease shorter than the work it covers
    /// would let a second execution claim a period the first is still generating, which is the
    /// duplicate the claim exists to prevent. Fifteen minutes of call plus room for the reads
    /// around it.
    /// </summary>
    /// <remarks>
    /// Shorter than it could be, on purpose. The expiry is the self-healing half of the claim —
    /// an execution killed mid-generation by a deploy, an OOM or the job's own timeout never
    /// reaches its release — so every minute here is a minute a member's period stays blocked
    /// after a crash. Twenty leaves the block shorter than the hour the Cloud Run job may take to
    /// notice it has died, and well inside a day for the daily books.
    /// </remarks>
    public static readonly TimeSpan Default = TimeSpan.FromMinutes(20);
}
