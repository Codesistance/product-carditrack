using CardiTrack.Domain.Common;

namespace CardiTrack.Domain.Entities;

/// <summary>
/// One caregiver's attempt to add one CardiMember, named by a key their client chose, and the
/// member that attempt produced.
/// </summary>
/// <remarks>
/// <para>
/// The problem it solves is narrow and specific: a create whose commit the database accepted but
/// whose acknowledgement never reached the phone. The caregiver sees a failure, taps Continue
/// again, and without this row the second attempt makes a second member and a second link — two
/// people in the care circle where the caregiver meant one, and no way for them to tell which is
/// which.
/// </para>
/// <para>
/// The row is written <em>inside the same transaction as the member and the link</em>, which is
/// what makes it answer the question the caregiver's retry is really asking. If the commit landed,
/// this landed with it and the retry finds it; if the commit did not land, neither did this, and
/// the retry creates the member as though the first attempt never happened. There is no third
/// state to reason about.
/// </para>
/// <para>
/// The key is the client's to choose and this service never interprets it — it is compared, never
/// parsed. It is scoped by <see cref="UserId"/> so one caregiver's key can never collide with or
/// reveal another's, and unique within that scope, so two simultaneous attempts cannot both win.
/// </para>
/// <para>
/// Holds no health data: a caregiver id, a member id, and an opaque string. Both ids already sit
/// together permanently in <c>UserCardiMembers</c>, so this adds no fact about anyone that the
/// database did not already hold — see the DPIA's processing inventory.
/// </para>
/// </remarks>
public class CardiMemberCreationKey : BaseEntity
{
    /// <summary>The caregiver who made the attempt. Keys are unique within this scope, not globally.</summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// The client's own name for this attempt, opaque to the server. Long enough for a GUID in any
    /// of its spellings, short enough that it cannot be used as storage.
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>The member the attempt created, which is what a retry gets handed back.</summary>
    public Guid CardiMemberId { get; set; }
}
