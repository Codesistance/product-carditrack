using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.DTOs.Responses;

public class OrganizationResponse
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The code to share so somebody can ask to join, in display form (<c>KTR7-M2Q9</c>). Rendered
    /// here rather than stored that way — the column holds it unseparated so lookups compare like
    /// with like, and the hyphen is only ever for reading.
    /// </summary>
    public string FamilyId { get; set; } = string.Empty;

    public OrganizationType Type { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedDate { get; set; }
    public SubscriptionResponse? Subscription { get; set; }
}

public class SubscriptionResponse
{
    public Guid Id { get; set; }
    public SubscriptionTier Tier { get; set; }
    public SubscriptionStatus Status { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime? TrialEndDate { get; set; }
    public int MaxCardiMembers { get; set; }
    public int MaxUsers { get; set; }
}
