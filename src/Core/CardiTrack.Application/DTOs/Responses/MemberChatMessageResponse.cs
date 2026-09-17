using CardiTrack.Application.DTOs.Common;

namespace CardiTrack.Application.DTOs.Responses;

public class MemberChatMessageResponse
{
    public required Guid SessionId { get; init; }
    public required string Reply { get; init; }
    public required IReadOnlyList<ChartSeries> Charts { get; init; }
    public required DateTimeOffset GeneratedAt { get; init; }

    /// <summary>
    /// True when this reply applied a change to the member's alert settings — a rule switched, an
    /// alarm added, changed or removed — so a client holding a cached settings page knows to
    /// refresh it. Optional with a default, so a client built before it existed reads the reply
    /// as before.
    /// </summary>
    public bool ChangedAlertSettings { get; init; }

    /// <summary>True when this turn deleted or replaced a CardiJournal book. A client holding a
    /// cached Journal tab can refresh it; the API uses it to name the audit action.</summary>
    public bool ChangedJournal { get; init; }
}
