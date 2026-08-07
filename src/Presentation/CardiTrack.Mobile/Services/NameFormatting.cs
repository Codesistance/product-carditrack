namespace CardiTrack.Mobile.Services;

/// <summary>
/// Shared name rendering for avatars and greetings. One place so the dashboard hero card and
/// the member detail screen can't disagree about someone's initials.
/// </summary>
public static class NameFormatting
{
    /// <summary>First + last initial ("Margaret Doe" → "MD"). Photos are not stored yet.</summary>
    public static string Initials(string? name)
    {
        var parts = (name ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => "?",
            1 => char.ToUpperInvariant(parts[0][0]).ToString(),
            _ => $"{char.ToUpperInvariant(parts[0][0])}{char.ToUpperInvariant(parts[^1][0])}",
        };
    }

    /// <summary>The name people are called by in copy — "Call Margaret", "Reach out to Margaret now".</summary>
    public static string FirstName(string? name)
    {
        var parts = (name ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? string.Empty : parts[0];
    }
}
