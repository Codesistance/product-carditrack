using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.DTOs.Responses;

/// <summary>A member's medical information: what is on file now, and what used to be.</summary>
public class MedicalEntriesResponse
{
    /// <summary>The lines on file now, grouped by kind (conditions, allergies, medications, other), oldest first within each.</summary>
    public List<MedicalEntryResponse> Current { get; set; } = [];

    /// <summary>Lines that were changed or taken off, most recently removed first.</summary>
    public List<MedicalEntryResponse> History { get; set; } = [];

    /// <summary>
    /// When the whole list was last known to hold: the oldest confirmation among the current
    /// lines, or null when any of them has never been confirmed (or there are none). The same
    /// value as <c>CardiMemberDetailResponse.MedicalNotesReviewedAtUtc</c>.
    /// </summary>
    public DateTime? ReviewedAtUtc { get; set; }
}

/// <summary>One line of a member's medical information.</summary>
public class MedicalEntryResponse
{
    public Guid Id { get; set; }
    public MedicalEntryKind Kind { get; set; }
    public string Text { get; set; } = string.Empty;
    public DateTime AddedAtUtc { get; set; }

    /// <summary>The caregiver who wrote it, or null when it was carried over from the single note or they have left.</summary>
    public string? AddedByName { get; set; }

    /// <summary>When somebody last said it still holds; null when nobody has.</summary>
    public DateTime? ConfirmedAtUtc { get; set; }

    /// <summary>When it left the current list; null for a current line.</summary>
    public DateTime? RemovedAtUtc { get; set; }

    /// <summary>The caregiver who changed or removed it, when known.</summary>
    public string? RemovedByName { get; set; }

    /// <summary>True when an edit replaced it (a "changed" line), false when it was simply removed.</summary>
    public bool WasChanged { get; set; }
}
