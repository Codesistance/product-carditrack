using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;

namespace CardiTrack.Application.Interfaces.Services;

/// <summary>
/// The wearer-side half of device onboarding: a caregiver mints an invitation, hands it over as a
/// link or a QR code, and the wearer authorizes their own wearable from their own device.
/// </summary>
/// <remarks>
/// <para>
/// Two audiences, and the split down the middle of this interface is the security boundary. The
/// first three methods are the caregiver's, called from authenticated routes and authorized the way
/// every other CardiMember surface is — by an active link, checked on every call. The last three are
/// the wearer's, called from anonymous routes where the token is the entire authorization.
/// </para>
/// <para>
/// What that asymmetry buys: the wearer-facing methods can only ever advance one named invite
/// towards one named member's one named brand. They take no member id, no user id and no provider —
/// there is nothing in their signatures for a caller to substitute. Holding a token lets you
/// authorize the device that token was minted for, decline it, or read the four facts on
/// <see cref="WearerInviteView"/>. It does not let you read anything, reach any other member, or
/// name a brand of your own choosing.
/// </para>
/// </remarks>
public interface IDeviceConnectionInviteService
{
    /// <summary>
    /// Mints an invitation and returns it with its one-time URL. Supersedes any live invite for the
    /// same member and brand — asking for a new one is how a caregiver takes back an old one.
    /// </summary>
    /// <param name="requestBaseUrl">
    /// The API's own public origin, used to build the wearer's URL when nothing is configured.
    /// Supplied by the controller from the request it is answering, and ignored whenever
    /// configuration names a base explicitly — which is what deployed environments do, so a
    /// forged Host header cannot decide where an invitation points.
    /// </param>
    Task<DeviceInviteResponse> CreateAsync(
        Guid requestingUserId,
        Guid cardiMemberId,
        CreateDeviceInviteRequest request,
        string requestBaseUrl,
        CancellationToken ct = default);

    /// <summary>
    /// One invite's current state, for the caregiver's waiting screen. Never re-issues the URL.
    /// </summary>
    Task<DeviceInviteResponse> GetAsync(
        Guid requestingUserId, Guid cardiMemberId, Guid inviteId, CancellationToken ct = default);

    /// <summary>
    /// Withdraws a live invite. Idempotent in effect: revoking one that has already finished leaves
    /// its outcome alone and reports that outcome back, because a caregiver cancelling a
    /// connection that landed a second earlier should not be told the cancel failed.
    /// </summary>
    Task<DeviceInviteResponse> RevokeAsync(
        Guid requestingUserId, Guid cardiMemberId, Guid inviteId, CancellationToken ct = default);

    /// <summary>
    /// What the wearer's page may say, or null when the token names no live invite — unknown,
    /// expired, already used, declined or revoked all come back the same way, so the page cannot be
    /// used to tell one from another.
    /// </summary>
    Task<WearerInviteView?> ViewAsync(string token, CancellationToken ct = default);

    /// <summary>
    /// The wearer said yes. Marks the invite opened, mints PKCE state bound to it, and returns the
    /// provider's authorization URL to send them to. Null when the token names no live invite.
    /// </summary>
    Task<string?> StartAsync(string token, CancellationToken ct = default);

    /// <summary>
    /// The wearer said it was not them. Returns whether a live invite was there to decline; either
    /// way the page that follows says the same thing, so a caller cannot probe with this.
    /// </summary>
    Task<bool> DeclineAsync(string token, CancellationToken ct = default);

    /// <summary>
    /// Finishes a wearer-channel callback the provider has bounced back: exchanges the code, stores
    /// the connection, and closes the invitation out.
    /// </summary>
    /// <remarks>
    /// Takes <paramref name="error"/> as well as <paramref name="code"/> because a refusal is a
    /// outcome the wearer needs told, not an exception: they tapped "no" on Google's screen, or
    /// their account cannot share what we asked for. Those leave the invitation live so they can
    /// change their mind without the caregiver having to send another.
    /// </remarks>
    Task<WearerConnectionOutcome> CompleteFromCallbackAsync(
        string provider, string state, string? code, string? error, CancellationToken ct = default);
}
