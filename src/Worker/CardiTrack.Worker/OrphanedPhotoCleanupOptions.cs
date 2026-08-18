namespace CardiTrack.Worker;

public class OrphanedPhotoCleanupOptions
{
    /// <summary>
    /// Log what would be deleted and delete nothing — blob and database alike. The rehearsal
    /// switch the data-protection ADR requires of every destructive job
    /// (docs/technical/data_protection_architecture.md §5.2): the first run against a real
    /// estate should produce a reviewable list, not a fait accompli.
    /// </summary>
    public bool DryRun { get; set; } = false;
}
