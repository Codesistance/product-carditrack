using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Interfaces.Services;

/// <summary>
/// Wearable connection lifecycle for the M1-05..M1-07 wizard: list a CardiMember's devices,
/// initiate a PKCE server-OAuth flow, and complete it by exchanging the authorization code.
/// </summary>
public interface IDeviceConnectionService
{
    Task<DeviceListResponse> GetDevicesAsync(Guid requestingUserId, Guid cardiMemberId, CancellationToken ct = default);

    Task<OAuthInitiationResponse> InitiateConnectionAsync(
        Guid requestingUserId, Guid cardiMemberId, ConnectDeviceRequest request, CancellationToken ct = default);

    /// <summary>
    /// Decides what the anonymous oauth/redirect bounce should do with a callback, from the state
    /// token it carries, without consuming that state. Returns null for unknown providers, unknown
    /// or expired state, or an app redirect that fails validation.
    ///
    /// One registered redirect URI serves both the in-app flow and the wearer's own browser, so the
    /// bounce cannot know which it is answering until this has told it — see
    /// <see cref="DeviceOAuthCallbackTarget"/>. For the app flow, what comes back is safe for the
    /// caller to append callback parameters to: always an absolute app-scheme URI with no fragment.
    /// </summary>
    Task<DeviceOAuthCallbackTarget?> ResolveCallbackTargetAsync(
        string provider, string state, CancellationToken ct = default);

    /// <summary>
    /// Mints PKCE state for a wearer authorizing from their own browser, and returns the provider
    /// authorization URL to send them to.
    /// </summary>
    /// <remarks>
    /// The same flow as <see cref="InitiateConnectionAsync"/> with one difference that matters: the
    /// code verifier stays in the server-side state rather than travelling to a client. In the app
    /// flow the phone holds it and proves possession by posting it back over an authenticated
    /// request. A browser on a wearer's phone has no account and no authenticated request to post it
    /// on, so handing it over would put the verifier in a page whose return we cannot authenticate —
    /// which is the one thing PKCE exists to prevent.
    /// </remarks>
    Task<string> InitiateWearerConnectionAsync(
        Guid inviteId,
        Guid creatingUserId,
        Guid cardiMemberId,
        DeviceType deviceType,
        Guid? replacesConnectionId,
        CancellationToken ct = default);

    /// <summary>
    /// Checks that <paramref name="requestingUserId"/> may replace <paramref name="deviceId"/> on
    /// this member — the same primary-caregiver rule as removing it, since a replacement removes
    /// it — so an invitation to replace a device fails when it is created rather than after the
    /// wearer has already given consent.
    /// </summary>
    Task EnsureCanReplaceAsync(
        Guid requestingUserId, Guid cardiMemberId, Guid deviceId, CancellationToken ct = default);

    /// <summary>
    /// The invitation a pending wearer state was minted for, without consuming the state. Null when
    /// the state is unknown, expired, already spent, or not a wearer state.
    /// </summary>
    /// <remarks>
    /// Exists so the invitation's own liveness can be checked <em>before</em> the code is exchanged.
    /// The state outlives a revocation — it is cached for fifteen minutes and knows nothing about
    /// the row — so without this a caregiver who cancels while the wearer is still on the provider's
    /// consent screen would be overruled by the wearer finishing.
    /// </remarks>
    Task<Guid?> PeekWearerInviteIdAsync(
        string provider, string state, CancellationToken ct = default);

    /// <summary>
    /// Completes a wearer-channel callback server-side: consumes the state, exchanges the code, and
    /// stores the connection under the member and caregiver the invite named.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The caregiver's access is re-checked here, not merely at invite creation. An invite can
    /// outlive the authority that issued it — a caregiver removed from the care circle, a member
    /// deactivated — and the grant this completes would otherwise start flowing health data to
    /// somebody who had already been cut off.
    /// </para>
    /// <para>
    /// Returns the invite it was minted for alongside the connection, so the caller can close the
    /// invite out. Throws <see cref="Exceptions.DeviceConnectionException"/> with
    /// <see cref="Exceptions.DeviceConnectionException.InvalidStateToken"/> when the state is
    /// unknown, expired, already spent, or not a wearer state.
    /// </para>
    /// </remarks>
    Task<WearerConnectionCompletion> CompleteWearerConnectionAsync(
        string provider, string state, string code, CancellationToken ct = default);

    Task<DeviceResponse> CompleteConnectionAsync(
        Guid requestingUserId, string provider, OAuthCallbackRequest request, CancellationToken ct = default);

    /// <summary>
    /// Disconnects a device (M1-15 "Remove Device"): soft-deletes the connection and discards
    /// its stored OAuth tokens, so revoking access at the provider is the user's only remaining
    /// step. Promotes another connection to primary if this one was it.
    /// </summary>
    Task DisconnectAsync(
        Guid requestingUserId, Guid cardiMemberId, Guid deviceId, CancellationToken ct = default);

    /// <summary>Makes this the CardiMember's primary device, demoting the previous one (M1-15).</summary>
    Task<DeviceResponse> SetPrimaryAsync(
        Guid requestingUserId, Guid cardiMemberId, Guid deviceId, CancellationToken ct = default);

    /// <summary>
    /// M1-15 "Refresh Connection": renews the OAuth token if it has expired and reports the
    /// connection's current state. Deliberately does <em>not</em> pull health data — that is the
    /// sync worker's job, per the background-job rule in CLAUDE.md.
    /// </summary>
    Task<DeviceResponse> RefreshConnectionAsync(
        Guid requestingUserId, Guid cardiMemberId, Guid deviceId, CancellationToken ct = default);

    /// <summary>
    /// M1-15 "Suspend": stops the connection collecting — no syncs, webhook pulls, auth recovery
    /// or device nudges — while keeping its tokens and history, until it is resumed. Open-ended,
    /// because the member's other devices go on collecting; for the same reason it is refused
    /// for the member's only collecting device, where Pause Monitoring (bounded) is the tool.
    /// A suspended primary hands the flag to another collecting device.
    /// </summary>
    Task<DeviceResponse> SuspendAsync(
        Guid requestingUserId, Guid cardiMemberId, Guid deviceId, CancellationToken ct = default);

    /// <summary>
    /// M1-15 "Resume": the connection collects again from the sync worker's next pass. Takes the
    /// primary flag back only when the member has no primary.
    /// </summary>
    Task<DeviceResponse> ResumeAsync(
        Guid requestingUserId, Guid cardiMemberId, Guid deviceId, CancellationToken ct = default);
}
