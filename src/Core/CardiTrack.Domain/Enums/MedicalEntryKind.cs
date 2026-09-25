namespace CardiTrack.Domain.Enums;

/// <summary>
/// What a line of a member's medical information is about. Stored by name — see
/// <c>MedicalEntryConfiguration</c>.
/// </summary>
/// <remarks>
/// Deliberately few. These are headings a caregiver arriving in a hurry scans for, not a clinical
/// coding scheme: an allergy and a medication are the two things a paramedic asks first, a
/// condition is everything else long-standing, and <see cref="Other"/> is whatever the family
/// wants on file that fits none of them — which is also where notes written before the ledger
/// existed are filed, since nobody said what they were.
/// </remarks>
public enum MedicalEntryKind
{
    Condition = 1,
    Allergy = 2,
    Medication = 3,
    Other = 4,
}
