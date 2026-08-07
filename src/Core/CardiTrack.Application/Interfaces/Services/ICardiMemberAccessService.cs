namespace CardiTrack.Application.Interfaces.Services;

/// <summary>
/// Single authority for "may this user read this CardiMember's health data?".
/// Every surface that returns, narrates, or exports member health data must go through
/// this before touching the data — see docs/technical/data_protection_architecture.md.
/// </summary>
/// <remarks>
/// Access is granted by an active <c>UserCardiMember</c> link carrying
/// <c>CanViewHealthData</c>. Denial is reported as <see cref="KeyNotFoundException"/> so
/// callers surface a 404 rather than a 403: a 403 would confirm that the requested
/// CardiMember exists, which is itself a disclosure.
/// </remarks>
public interface ICardiMemberAccessService
{
    /// <summary>Returns whether the user may read this CardiMember's health data.</summary>
    Task<bool> HasViewAccessAsync(Guid requestingUserId, Guid cardiMemberId, CancellationToken ct = default);

    /// <summary>Throws <see cref="KeyNotFoundException"/> unless the user may read this CardiMember's health data.</summary>
    Task RequireViewAccessAsync(Guid requestingUserId, Guid cardiMemberId, CancellationToken ct = default);

    /// <summary>
    /// Throws <see cref="KeyNotFoundException"/> unless the user may read <em>every</em> requested
    /// CardiMember's health data. All-or-nothing: a partial result would still disclose which of
    /// the requested members the user is linked to.
    /// </summary>
    Task RequireViewAccessAsync(
        Guid requestingUserId, IReadOnlyCollection<Guid> cardiMemberIds, CancellationToken ct = default);
}
