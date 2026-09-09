namespace CardiTrack.Domain.Enums;

/// <summary>Where a caregiver-requested history re-pull is in its life.</summary>
public enum HistoryRepullStatus
{
    /// <summary>Recorded by the API; the Worker has not picked it up yet.</summary>
    Pending = 1,

    /// <summary>The Worker has fetched at least one chunk, or is between chunks.</summary>
    InProgress = 2,

    /// <summary>Every day in the range was fetched (or confirmed empty).</summary>
    Completed = 3,

    /// <summary>The provider kept refusing; the days already stored stay stored.</summary>
    Failed = 4,

    /// <summary>Dropped before finishing — monitoring was paused or the connection went away.</summary>
    Cancelled = 5,
}
