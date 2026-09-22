namespace CardiTrack.Application.DTOs.Responses;

/// <summary>
/// One answer somebody in the family gave an alert, as the detail screen renders it.
/// </summary>
public class AlertResponseEntry
{
    public Guid Id { get; set; }

    /// <summary>acknowledge / close.</summary>
    public string Kind { get; set; } = string.Empty;

    public Guid UserId { get; set; }

    /// <summary>Who answered, as the family knows them. Falls back to "Someone" for a deleted account.</summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>The stored code, or null when they only wrote a note.</summary>
    public string? ResponseCode { get; set; }

    /// <summary>
    /// The code's wording as this build knows it, or null for a code the catalogue no longer
    /// offers.
    /// </summary>
    /// <remarks>
    /// Resolved here rather than by the client so a re-worded chip reads the new way everywhere at
    /// once. A null label with a non-null code is a retired option, not a bug — the screen shows
    /// the note and the attribution and simply has no chip text to print.
    /// </remarks>
    public string? ResponseLabel { get; set; }

    public string? Note { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// The canned answers this alert's rule offers, one short list per action.
/// </summary>
public class AlertResponseOptionsResponse
{
    public List<AlertResponseOptionResponse> Acknowledge { get; set; } = [];
    public List<AlertResponseOptionResponse> Close { get; set; } = [];
}

/// <summary>One chip: the code that gets stored, and the words on it.</summary>
public class AlertResponseOptionResponse
{
    public string Code { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
}
