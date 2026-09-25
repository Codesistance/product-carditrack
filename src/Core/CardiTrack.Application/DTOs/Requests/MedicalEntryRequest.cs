using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.DTOs.Requests;

/// <summary>A line of a member's medical information, as added or as changed.</summary>
public class MedicalEntryRequest
{
    public MedicalEntryKind Kind { get; set; } = MedicalEntryKind.Other;

    /// <summary>What the line says — one condition, allergy or medication, not a whole history.</summary>
    public string Text { get; set; } = string.Empty;
}
