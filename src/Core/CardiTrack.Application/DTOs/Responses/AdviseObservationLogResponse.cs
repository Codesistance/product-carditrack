using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.DTOs.Responses;

/// <summary>
/// What the Advise pass has noticed about one CardiMember over a window — the record a caregiver
/// takes to an appointment, as against <see cref="AdviseResponse"/>'s single current suggestion.
/// </summary>
public class AdviseObservationLogResponse
{
    public required Guid CardiMemberId { get; init; }

    /// <summary>The window actually served, after clamping — never the one that was asked for
    /// when those differ, so a client always knows what it is showing.</summary>
    public required DateTimeOffset From { get; init; }
    public required DateTimeOffset To { get; init; }

    /// <summary>Newest first. Empty when nothing was noticed in the window, which is a real and
    /// unremarkable answer — a member whose readings held steady has an empty log.</summary>
    public required IReadOnlyList<AdviseObservationResponse> Observations { get; init; }

    /// <summary>
    /// True when the window held more entries than were returned. The client says so rather than
    /// implying the list is the whole of it — a report that silently drops the older half of a
    /// year is worse than one that admits it was trimmed.
    /// </summary>
    public required bool Truncated { get; init; }
}

/// <summary>One dated entry in the log.</summary>
public class AdviseObservationResponse
{
    public required AdviseTopic Topic { get; init; }

    /// <summary>What was noticed in the readings, in the words the family saw at the time.</summary>
    public required string Summary { get; init; }

    /// <summary>What was suggested alongside it, kept so the entry reads as it did on the day.</summary>
    public required string Suggestion { get; init; }

    /// <summary>Which wellness reference it drew on, when one was named.</summary>
    public string? GuidelineCited { get; init; }

    public required DateTimeOffset ObservedAt { get; init; }
}
