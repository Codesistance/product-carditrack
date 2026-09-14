using CardiTrack.Domain.Common;
using CardiTrack.Domain.Enums;
using CardiTrack.Domain.Interfaces;

namespace CardiTrack.Domain.Entities;

public class User : BaseEntity, ISoftDeletable
{
    public Guid OrganizationId { get; set; }
    public string Auth0UserId { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public UserRole Role { get; set; } = UserRole.Member;
    public bool EmailVerified { get; set; }
    public DateTime? LastLoginDate { get; set; }
    public bool IsActive { get; set; } = true;

    // UTC timestamp of when the user dismissed the Google-required health-data
    // disclosure banner; null means it must still be shown
    public DateTime? HealthDataDisclosureDismissedDate { get; set; }

    /// <summary>
    /// When this caregiver asked for their account to be deleted, or null if they have not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A request, not the deletion. The published policy promises erasure <em>within 30 days of a
    /// verified request</em>, and the 30 days are deliberately spent rather than merely allowed:
    /// the account is signed out and unusable from the moment this is stamped, and signing back in
    /// during the window cancels it by clearing this field. These are people watching over a
    /// relative’s health, and a tap at 2am is recoverable for a month rather than final.
    /// </para>
    /// <para>
    /// Nullable and cleared on cancellation rather than paired with a status enum: the two states
    /// are “requested at T” and “not requested”, and a second column could disagree with this one.
    /// The purge itself is M6’s worker, which reads this field — until that ships, this records the
    /// request and the manual runbook fulfils it.
    /// </para>
    /// </remarks>
    public DateTime? DeletionRequestedAtUtc { get; set; }

    // Locale preferences
    public string Locale { get; set; } = "en-US";
    public string TimeZoneId { get; set; } = "UTC";

    // Navigation properties
    public ICollection<UserCardiMember> UserCardiMembers { get; set; } = new List<UserCardiMember>();
}
