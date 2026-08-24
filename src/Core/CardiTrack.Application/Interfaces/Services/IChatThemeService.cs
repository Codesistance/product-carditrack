namespace CardiTrack.Application.Interfaces.Services;

/// <summary>
/// Labels completed member-chat conversations after the fact — the short "what was this about"
/// line the history list titles its rows by. Runs as a scheduled pass on the GCP pipeline host
/// (the sanctioned home for AI background work), never inside a request.
/// </summary>
public interface IChatThemeService
{
    /// <summary>
    /// One pass: finds completed, unthemed conversations (bounded batch), generates a short
    /// label for each on the Rewrite slot from a name-redacted transcript, and persists it
    /// encrypted at rest. A conversation whose generation fails is skipped and picked up by a
    /// later pass. Returns how many conversations were themed.
    /// </summary>
    Task<int> ThemeDueSessionsAsync(DateTime utcNow, CancellationToken ct = default);
}
