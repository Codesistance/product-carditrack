namespace CardiTrack.Application.DTOs.Responses;

/// <summary>
/// What the provider's https bounce should do with a callback, resolved from the state token it
/// carries.
/// </summary>
/// <remarks>
/// <para>
/// One registered redirect URI now serves two different flows, so the bounce has to ask which it is
/// answering before it does anything. The state token is the only thing on the request that can
/// tell it — the provider sends back exactly what we sent it, and everything else in the query is
/// the provider's own.
/// </para>
/// <para>
/// Resolved from the state without consuming it. Single-use consumption belongs to whichever path
/// then completes the flow, so that a callback the bounce cannot act on has not already spent the
/// wearer's one chance to finish.
/// </para>
/// </remarks>
/// <param name="IsWearerFlow">
/// True when the state was minted for a wearer's own browser, which the bounce finishes itself.
/// False for the app flow, where the browser is handed back to the phone and the app posts the code.
/// </param>
/// <param name="AppRedirectUri">
/// The app deep link to bounce into, for the app flow only. Always an absolute
/// <c>carditrack://</c> URI with no fragment — the caller appends query parameters to it — and
/// always null for a wearer flow, which has no app to return to.
/// </param>
public sealed record DeviceOAuthCallbackTarget(bool IsWearerFlow, string? AppRedirectUri);
