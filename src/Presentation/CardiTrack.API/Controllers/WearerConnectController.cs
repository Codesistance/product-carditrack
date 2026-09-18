using System.Net.Mime;
using CardiTrack.API.Infrastructure.OAuth;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Infrastructure.Settings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace CardiTrack.API.Controllers;

/// <summary>
/// The wearer's half of device onboarding: three anonymous pages, reached by following a caregiver's
/// invitation link or scanning their QR code.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Anonymous by necessity.</strong> The person these pages are for has no CardiTrack account
/// and — by a product decision taken 2026-08-10 — is never going to have one. Their only previous
/// contact with the product was Google's consent screen appearing on somebody else's phone. So the
/// invitation token is the whole of the authorization, backed by a per-IP rate limit on these routes
/// and by the token being 256 bits of randomness that exists for hours.
/// </para>
/// <para>
/// <strong>What holding a token gets you is bounded by the service, not by this controller.</strong>
/// Every method here passes the token straight to
/// <see cref="IDeviceConnectionInviteService"/> and renders what comes back. There is no member id,
/// user id or provider anywhere in these signatures for a caller to substitute, and no branch that
/// returns more for one token than another. The most any of it discloses is the four facts on the
/// consent page: two first names, a brand, and a deadline.
/// </para>
/// <para>
/// <strong>The token travels in the query string</strong>, which is normally where credentials
/// should not go. Two things make it the right place here and one makes it safe: a QR code and a
/// shared link have nowhere else to put it, request logging strips query strings entirely
/// (<c>ApmExtensions</c>), the audit trail records paths only, and every page sends
/// <c>Referrer-Policy: no-referrer</c> so it cannot leak onward. What remains is browser history on
/// the wearer's own device, against a credential that expires.
/// </para>
/// <para>
/// Deliberately not marked <c>[AuditHealthDataAccess]</c>: the middleware behind that attribute
/// requires an authenticated subject and would silently record nothing here. The invite service
/// writes these entries itself, against the caregiver who is accountable for the invitation.
/// </para>
/// <para>
/// <strong>The two POSTs carry no anti-forgery token, and need none.</strong> A forged cross-site
/// post would have to carry the invitation token in its body to do anything at all — and anyone who
/// has that token can already call these endpoints directly. There is no ambient credential here for
/// a forgery to ride on, which is the thing the protection exists to stop. What the pages do rely on
/// is the <c>form-action 'self'</c> in their content security policy, so a content injection could
/// not aim one of our own forms at somebody else's host.
/// </para>
/// </remarks>
[ApiController]
[AllowAnonymous]
[Route("connect")]
public class WearerConnectController : ControllerBase
{
    private readonly IDeviceConnectionInviteService _invites;
    private readonly IOptions<DeviceInviteOptions> _options;
    private readonly IOptions<List<DeviceProviderSettings>> _providers;
    private readonly ILogger<WearerConnectController> _logger;

    public WearerConnectController(
        IDeviceConnectionInviteService invites,
        IOptions<DeviceInviteOptions> options,
        IOptions<List<DeviceProviderSettings>> providers,
        ILogger<WearerConnectController> logger)
    {
        _invites = invites;
        _options = options;
        _providers = providers;
        _logger = logger;
    }

    /// <summary>
    /// The origins the consent form is allowed to end up at, taken from the configured providers'
    /// authorization URLs.
    /// </summary>
    /// <remarks>
    /// "Continue" posts here and is redirected straight to the provider, and browsers apply
    /// <c>form-action</c> across that redirect — so a policy naming only this host refuses the
    /// submission, silently. Read from configuration rather than written down here so that a
    /// provider added later is permitted by being configured, which is the only place its consent
    /// screen is named anyway.
    /// </remarks>
    private IReadOnlyList<string> ProviderFormActionOrigins() =>
        _providers.Value
            .Select(p => p.AuthorizationUrl)
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Select(url => Uri.TryCreate(url, UriKind.Absolute, out var parsed) ? parsed.GetLeftPart(UriPartial.Authority) : null)
            .Where(origin => origin is not null)
            .Select(origin => origin!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>The consent ask — what the invitation link opens.</summary>
    [HttpGet("")]
    [Produces(MediaTypeNames.Text.Html)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Ask([FromQuery(Name = "t")] string? t, CancellationToken ct)
    {
        var view = await _invites.ViewAsync(t ?? string.Empty, ct);
        if (view is null)
            return WearerConnectPage.NotAvailable(Response);

        return WearerConnectPage.Ask(
            Response, view, t!, _options.Value.PrivacyPolicyUrl, ProviderFormActionOrigins());
    }

    /// <summary>
    /// "Yes, that's me" — sends the wearer on to the provider's own consent screen.
    /// </summary>
    /// <remarks>
    /// A POST, not a link, because it changes something: it marks the invitation opened and mints
    /// PKCE state. A GET here would let any preview fetch — a messaging app unfurling the link, a
    /// mail scanner — start the flow before the wearer had read a word of it.
    /// </remarks>
    [HttpPost("start")]
    [Produces(MediaTypeNames.Text.Html)]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Start([FromForm(Name = "t")] string? t, CancellationToken ct)
    {
        string? authorizationUrl;
        try
        {
            authorizationUrl = await _invites.StartAsync(t ?? string.Empty, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The same page an unknown token gets, and for the same reason. A provider with no
            // configured bounce redirect throws here, so rendering anything distinctive would tell
            // a caller that this token was real and merely unusable, while an invented one returned
            // the byte-identical 404 — which is exactly the difference the rest of this controller
            // is built to hide. The wearer loses nothing: their invitation is still live, so
            // reopening the link brings the ask back.
            _logger.LogError(ex, "Failed to start a wearer device connection.");
            return WearerConnectPage.NotAvailable(Response);
        }

        if (authorizationUrl is null)
            return WearerConnectPage.NotAvailable(Response);

        // The provider's own URL, built by us from configuration — never anything the request
        // supplied — so this redirect cannot be steered by its caller.
        return Redirect(authorizationUrl);
    }

    /// <summary>"This isn't me" — ends the invitation and tells the caregiver.</summary>
    [HttpPost("decline")]
    [Produces(MediaTypeNames.Text.Html)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Decline([FromForm(Name = "t")] string? t, CancellationToken ct)
    {
        // The same page whether or not there was anything to decline. Somebody who was sent this by
        // mistake should be told the matter is closed, and somebody probing tokens should learn
        // nothing from the difference.
        await _invites.DeclineAsync(t ?? string.Empty, ct);
        return WearerConnectPage.Declined(Response);
    }
}
