using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Core.Forms;

namespace CardiTrack.Mobile.Core.Alerts;

/// <summary>
/// How an answered alert describes itself: the strip across the top of its banner, and each row
/// of "What the family did".
/// </summary>
public static class AlertAnswerCopy
{
    public const string SettledOnItsOwn = "This settled on its own — no action needed";

    /// <summary>
    /// The strip across the top of the alert banner. Leads with the latest response when there is
    /// one, because with second caregivers "what did the family do" is the question (D-20); falls
    /// back to the first-wins acknowledgement, and to the system's own resolution when nobody
    /// touched it.
    /// </summary>
    /// <remarks>
    /// Who and when only — "Tom acknowledged · 2 minutes ago". The response's label ("Calling
    /// them now") used to ride along, which on a one-line strip made the headline the longest
    /// thing on the banner; the label and any note are read under "What the family did".
    /// </remarks>
    public static string? HandledLine(AlertDetailResponse alert)
    {
        ArgumentNullException.ThrowIfNull(alert);
        var handled = alert.Status is "acknowledged" or "resolved";
        if (!handled)
            return null;

        if (Latest(alert) is { } latest)
        {
            var line = $"{RowTitle(latest)} · {RelativeTime.Format(latest.CreatedAt)}";

            // A close the system had already made says so: the family's note is still worth
            // reading, but it did not end the alert.
            if (alert.Status == "resolved" && alert.ResolvedByUserId is null)
                line += ". It had already settled on its own.";
            return line;
        }

        if (alert.Status == "resolved" && alert.ResolvedByUserId is not null)
        {
            var closer = FirstName(alert.ResolvedByName) ?? "a caregiver";
            return $"Closed by {closer}";
        }

        if (alert.AcknowledgedAt is not { } at)
            return SettledOnItsOwn;

        var who = FirstName(alert.AcknowledgedByName) is { } name
            ? $"{name} acknowledged"
            : "Acknowledged";
        return $"{who} · {RelativeTime.Format(at)}";
    }

    /// <summary>
    /// Whether the episode is over — closed by a caregiver or settled on its own — rather than
    /// only acknowledged. The strip wears a different colour for each: an acknowledgement says
    /// somebody is on it, which is not the same news as it being finished.
    /// </summary>
    public static bool IsClosed(AlertDetailResponse alert)
    {
        ArgumentNullException.ThrowIfNull(alert);
        return alert.Status == "resolved";
    }

    /// <summary>"Tom acknowledged" / "Jane closed this".</summary>
    public static string RowTitle(AlertResponseEntry response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var who = FirstName(response.UserName) ?? "Someone";
        return string.Equals(response.Kind, "close", StringComparison.OrdinalIgnoreCase)
            ? $"{who} closed this"
            : $"{who} acknowledged";
    }

    /// <summary>
    /// The row's body: the code's label, then the note. A retired code has no label and the row
    /// carries the note alone; a response with neither is still a row, because the tap itself
    /// was the answer.
    /// </summary>
    public static string RowDetail(AlertResponseEntry response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var parts = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(response.ResponseLabel))
            parts.Add(response.ResponseLabel);
        if (!string.IsNullOrWhiteSpace(response.Note))
            parts.Add(response.Note.Trim());
        return parts.Count == 0 ? "No details given" : string.Join("\n", parts);
    }

    /// <summary>Newest first, as the server sends it — re-sorted here so a client never depends on that.</summary>
    public static IReadOnlyList<AlertResponseEntry> NewestFirst(IEnumerable<AlertResponseEntry> responses) =>
        responses.OrderByDescending(r => r.CreatedAt).ToList();

    private static AlertResponseEntry? Latest(AlertDetailResponse alert) =>
        alert.Responses.Count == 0 ? null : alert.Responses.MaxBy(r => r.CreatedAt);

    /// <summary>First name only — the family knows who Tom is, and the row is not a form.</summary>
    private static string? FirstName(string? name) =>
        (name ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
}
