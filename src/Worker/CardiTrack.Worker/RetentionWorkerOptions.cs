namespace CardiTrack.Worker;

public class RetentionWorkerOptions
{
    /// <summary>
    /// Log what would be erased and erase nothing — rows and bucket objects alike. The rehearsal
    /// switch the data-protection ADR requires of every destructive job
    /// (docs/technical/data_protection_architecture.md §5.2), and this is the job that needs it
    /// most: nothing it deletes can be recovered, and the first run against a real estate should
    /// produce a reviewable list rather than a fait accompli.
    /// </summary>
    public bool DryRun { get; set; } = false;

    /// <summary>
    /// Days a member chat conversation is kept after its newest turn — issue #488, decided
    /// 2026-09-14 and published in the privacy policy. Configurable so the published figure can
    /// be honoured without a deploy if it changes; shortening it deletes more on the next run,
    /// lengthening it keeps sessions this run would have taken.
    /// </summary>
    /// <remarks>
    /// The account grace period is deliberately <em>not</em> configurable here. It is
    /// <c>UserService.DeletionGracePeriod</c>, the same constant the app quotes to a caregiver
    /// when it tells them the date they can cancel until, and a worker that could disagree with
    /// that sentence would erase an account somebody still had the right to keep.
    /// </remarks>
    public int ChatRetentionDays { get; set; } = 90;

    /// <summary>
    /// Accounts erased, and chat sessions deleted, per run. Bounds how long one sweep holds a
    /// connection; whatever is left is picked up by the next run, since both passes select by
    /// timestamp and are re-entrant.
    /// </summary>
    public int BatchSize { get; set; } = 100;
}
