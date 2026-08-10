namespace CardiTrack.Application.Interfaces.Services;

/// <summary>
/// What one sync invocation is allowed to spend. The routine scope is what a caregiver waits on
/// (manual sync); the worker cadence carries the quota-heavier extras — the history backfill and
/// the sub-daily granular series — that belong to the background schedule, never to a user-facing
/// request.
/// </summary>
public enum SyncScope
{
    /// <summary>The routine window only: today plus the trailing repair days.</summary>
    Routine = 1,

    /// <summary>
    /// The routine window, plus one history-backfill chunk and the granular (minute-grain)
    /// series for each routine-window day.
    /// </summary>
    WorkerCadence = 2,
}
