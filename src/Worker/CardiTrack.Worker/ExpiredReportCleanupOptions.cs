namespace CardiTrack.Worker;

public class ExpiredReportCleanupOptions
{
    /// <summary>
    /// Log what would be deleted and delete nothing — bucket object and database row alike. The
    /// rehearsal switch the data-protection ADR requires of every destructive job
    /// (docs/technical/data_protection_architecture.md §5.2).
    /// </summary>
    public bool DryRun { get; set; } = false;

    /// <summary>
    /// Rows handled per run, for each of the two passes. Bounds how long one sweep holds a
    /// connection and how many bucket deletes a single failure can strand; whatever is left is
    /// picked up by the next run, since both passes select by timestamp and are re-entrant.
    /// </summary>
    public int BatchSize { get; set; } = 500;
}
