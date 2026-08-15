using CardiTrack.Domain.Common;

namespace CardiTrack.Domain.Entities;

/// <summary>
/// Per-CardiMember alert-rule enablement. Sparse: <see cref="DisabledRules"/> lists only the
/// rules a caregiver has turned off. Absence of a row — or an empty array — means every
/// known rule is on (the product default).
/// </summary>
public class AlertPreference : BaseEntity
{
    public Guid CardiMemberId { get; set; }

    /// <summary>
    /// JSON array of disabled rule id strings (e.g. <c>["late_bedtime","irregular_sleep"]</c>).
    /// Producers skip evaluation entirely for ids in this list.
    /// </summary>
    public string DisabledRules { get; set; } = "[]";
}
