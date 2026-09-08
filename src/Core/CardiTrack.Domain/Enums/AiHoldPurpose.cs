namespace CardiTrack.Domain.Enums;

/// <summary>
/// Which piece of model-backed work a <see cref="Entities.MemberAiHold"/> holds back for a
/// member. One value per generation path that has learned to stop retrying a prompt the model
/// cannot finish; a member can be held on one path and served normally on every other, because
/// the failure is a property of that path's prompt, not of the member.
/// </summary>
/// <remarks>
/// Persisted as its name (<c>MemberAiHoldConfiguration</c> maps it through
/// <c>HasConversion&lt;string&gt;()</c>), so adding a value needs no migration.
/// </remarks>
public enum AiHoldPurpose
{
    /// <summary>The family summary's clinical read, written by the digest job.</summary>
    FamilyDigest = 1,
}
