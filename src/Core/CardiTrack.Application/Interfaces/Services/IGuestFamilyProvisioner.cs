namespace CardiTrack.Application.Interfaces.Services;

/// <summary>
/// Gives a guest a family of their own, the first time they need one.
/// </summary>
/// <remarks>
/// <para>
/// A guest is somebody who signed up to join a family somebody else runs: an account with no
/// organization and no subscription. That is the right state for as long as they are only watching
/// a relative through another family — they are not paying, and there is nothing to pay for.
/// </para>
/// <para>
/// It stops being the right state the moment they add a CardiMember of their own, because a
/// member has to belong to a family and that family has to have a plan. So the family and its
/// trial are created here, at that moment, rather than at signup. The trial then starts when it
/// means something instead of expiring while somebody waits to be approved into a family they
/// never got a decision on.
/// </para>
/// </remarks>
public interface IGuestFamilyProvisioner
{
    /// <summary>
    /// The organization this user should create members in — theirs if they have one, or a newly
    /// created family if they are a guest.
    /// </summary>
    /// <remarks>
    /// Idempotent by construction: a user who already has a home organization gets it back
    /// unchanged, so a retried request cannot mint a second family.
    /// </remarks>
    Task<Guid> ResolveHomeOrganizationAsync(Guid userId, CancellationToken ct = default);
}
