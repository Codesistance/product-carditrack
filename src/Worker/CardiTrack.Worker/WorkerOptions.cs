namespace CardiTrack.Worker;

public class WorkerOptions
{
    public string CronExpression { get; set; } = "0 * * * * *";

    /// <summary>
    /// Run the job once immediately at startup, in addition to its cron schedule. Off by
    /// default — only jobs whose work is idempotent and cheap to repeat should opt in.
    /// </summary>
    public bool RunOnStartup { get; set; } = false;
}
