namespace CardiTrack.Domain.Enums;

/// <summary>
/// Where a <see cref="Entities.FamilyJoinRequest"/> has got to. Persisted by name.
/// </summary>
/// <remarks>
/// No Expired member, matching <see cref="CaregiverInviteStatus"/> and
/// <see cref="DeviceInviteStatus"/>: expiry is a fact about the clock rather than a transition
/// somebody performs.
/// </remarks>
public enum FamilyJoinRequestStatus
{
    /// <summary>Waiting on an admin.</summary>
    Pending = 1,

    /// <summary>An admin let them in. Terminal.</summary>
    Approved = 2,

    /// <summary>An admin said no. Terminal.</summary>
    Declined = 3,

    /// <summary>The asker changed their mind. Terminal.</summary>
    Withdrawn = 4,
}
