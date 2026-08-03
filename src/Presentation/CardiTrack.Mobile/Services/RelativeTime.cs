namespace CardiTrack.Mobile.Services;

public static class RelativeTime
{
    /// <summary>"just now", "10 minutes ago", "2 hours ago", "3 days ago".</summary>
    public static string Format(DateTime utcTimestamp)
    {
        var elapsed = DateTime.UtcNow - DateTime.SpecifyKind(utcTimestamp, DateTimeKind.Utc);
        if (elapsed < TimeSpan.Zero)
            elapsed = TimeSpan.Zero;

        return elapsed.TotalMinutes switch
        {
            < 1 => "just now",
            < 2 => "1 minute ago",
            < 60 => $"{(int)elapsed.TotalMinutes} minutes ago",
            < 120 => "1 hour ago",
            < 24 * 60 => $"{(int)elapsed.TotalHours} hours ago",
            < 48 * 60 => "1 day ago",
            _ => $"{(int)elapsed.TotalDays} days ago",
        };
    }
}
