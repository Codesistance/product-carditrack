namespace CardiTrack.Mobile.Core.Onboarding;

/// <summary>
/// The M1-04 add-CardiMember form as last typed. Nothing here exists server-side until the
/// form is submitted, so this is the one part of the wizard that can't be resumed from
/// onboarding status — it has to be kept on the device instead.
/// </summary>
public sealed class CardiMemberDraft
{
    public string? Name { get; set; }
    public DateTime? DateOfBirth { get; set; }
    public int RelationshipIndex { get; set; } = -1;

    /// <summary>
    /// Index into the add form's sex picker, or <c>-1</c> for unpicked. An index rather than a
    /// <c>Gender</c> for the same reason as <see cref="RelationshipIndex"/>: what is being
    /// restored is the state of a control, and a draft written before the picker existed
    /// deserialises to the unpicked sentinel rather than to a sex nobody chose.
    /// </summary>
    public int SexIndex { get; set; } = -1;

    public bool DetailsExpanded { get; set; }
    public string? MedicalNotes { get; set; }
    public string? EmergencyContactName { get; set; }
    public string? EmergencyContactPhone { get; set; }
    public string? PhotoPath { get; set; }
    public DateTime SavedUtc { get; set; }

    /// <summary>
    /// Anything worth restoring? Expanding the optional-details section on its own isn't
    /// input, so a draft holding only that is treated as empty.
    /// </summary>
    public bool HasContent =>
        !string.IsNullOrWhiteSpace(Name)
        || DateOfBirth is not null
        || RelationshipIndex >= 0
        || SexIndex >= 0
        || !string.IsNullOrWhiteSpace(MedicalNotes)
        || !string.IsNullOrWhiteSpace(EmergencyContactName)
        || !string.IsNullOrWhiteSpace(EmergencyContactPhone)
        || !string.IsNullOrEmpty(PhotoPath);
}
