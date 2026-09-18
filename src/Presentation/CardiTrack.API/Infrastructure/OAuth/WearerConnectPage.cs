using System.Net.Mime;
using System.Text.Encodings.Web;
using CardiTrack.Application.DTOs.Responses;
using Microsoft.AspNetCore.Mvc;

namespace CardiTrack.API.Infrastructure.OAuth;

/// <summary>
/// The pages a wearer sees when they follow a caregiver's invitation: the consent ask, and the four
/// things that can happen next.
/// </summary>
/// <remarks>
/// <para>
/// Server-rendered here rather than in the Blazor app for three reasons that all point the same way.
/// The API already owns the OAuth state, the provider configuration and the bounce these pages hand
/// off to; the Web app has no login, no API client for this, and talks to Postgres directly; and a
/// page with no script, no framework and no third-party asset is the smallest thing that can carry a
/// consent decision — which is what this is.
/// </para>
/// <para>
/// <strong>Every page here is served to whoever holds the link.</strong> The most that reaches one
/// is on <see cref="WearerInviteView"/>: two first names, a brand and a deadline. No page varies by
/// anything an attacker could learn from, which is why the four unhappy endings below are one method
/// with one piece of copy — an expired invitation, a spent one, a declined one and a token that
/// never existed must be indistinguishable, or this becomes a way to test tokens.
/// </para>
/// </remarks>
internal static class WearerConnectPage
{
    /// <summary>
    /// What the browser is allowed to do here, which is almost nothing. No script at all — these
    /// pages post plain forms — so a content injection has nothing to execute even if one were
    /// found, and no external origin can be reached to carry anything away.
    /// </summary>
    private const string ContentSecurityPolicy =
        "default-src 'none'; style-src 'unsafe-inline'; form-action 'self'; base-uri 'none'; frame-ancestors 'none'";

    /// <summary>
    /// Where each brand's wearer goes to take access back, keyed by the contract's wire name.
    /// </summary>
    /// <remarks>
    /// Keyed by brand rather than assumed, because "revoke here" is the one instruction on this page
    /// that is actively harmful when wrong: a Garmin wearer sent to Google's permissions screen
    /// would find no mention of CardiTrack and could reasonably conclude there was nothing to
    /// revoke. Fitbit and Pixel Watch both authorize through the Google Health API, so both land on
    /// the Google screen; a brand absent from here gets wording with no link at all.
    /// </remarks>
    private static readonly Dictionary<string, string> RevokeUrls = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fitbit"] = "https://myaccount.google.com/permissions",
        ["pixel_watch"] = "https://myaccount.google.com/permissions",
    };

    /// <summary>
    /// The consent ask: who is asking, for what, and the two ways to answer.
    /// </summary>
    /// <remarks>
    /// <paramref name="privacyPolicyUrl"/> is passed in rather than written as a relative link
    /// because the policy does not live on this host — the API serves no policy page, and a dead
    /// privacy link would be least forgivable on exactly this screen.
    /// </remarks>
    public static ContentResult Ask(
        HttpResponse response, WearerInviteView invite, string token, string privacyPolicyUrl)
    {
        var member = HtmlEncoder.Default.Encode(invite.MemberFirstName);
        var caregiver = HtmlEncoder.Default.Encode(invite.CaregiverFirstName);
        var device = HtmlEncoder.Default.Encode(invite.DeviceDisplayName);
        var safeToken = HtmlEncoder.Default.Encode(token);
        var privacy = HtmlEncoder.Default.Encode(privacyPolicyUrl);

        // "Stop sharing" is a link to the provider's own permissions screen, and each provider has
        // its own. Naming Google's for a brand that does not use it would send a Garmin wearer to a
        // page that has never heard of them — worse than saying nothing, because the one instruction
        // the page gives about taking access back would be wrong. Brands with no known screen get
        // wording that still tells them the control exists and where it lives.
        var revokeLink = RevokeUrls.TryGetValue(invite.Provider, out var url)
            ? $"""<a href="{url}" rel="noopener noreferrer">the account your {device} uses</a>"""
            : $"your {device} account settings";

        return Render(response, StatusCodes.Status200OK, $$"""
            <h1>{{caregiver}} would like to keep an eye on your health</h1>
            <p class="lede">
              They've asked CardiTrack to look after <strong>{{member}}</strong> using your
              <strong>{{device}}</strong>.
            </p>

            <h2>What CardiTrack would see</h2>
            <ul class="shares">
              <li><strong>Heart rate</strong> — so it can spot if something looks off</li>
              <li><strong>Activity and steps</strong> — to see you're keeping active</li>
              <li><strong>Sleep</strong> — how long and how well you rested</li>
              <li><strong>Your watch's battery</strong> — so nobody is caught out by a flat watch</li>
            </ul>
            <p class="note">
              {{caregiver}} sees these. CardiTrack never posts anything to your accounts and never
              sells your data. You can stop sharing at any time from {{revokeLink}}, and ask
              {{caregiver}} to remove the device.
            </p>

            <form method="post" action="/connect/start">
              <input type="hidden" name="t" value="{{safeToken}}">
              <button class="cta" type="submit">Yes, that's me — continue</button>
            </form>
            <form method="post" action="/connect/decline">
              <input type="hidden" name="t" value="{{safeToken}}">
              <button class="secondary" type="submit">This isn't me</button>
            </form>

            <p class="note small">
              You'll sign in with the account your {{device}} already uses. CardiTrack never sees
              your password.
              <a href="{{privacy}}" rel="noopener noreferrer">How CardiTrack handles your data</a>
            </p>
            """);
    }

    /// <summary>The grant landed and a connection was stored.</summary>
    public static ContentResult Connected(HttpResponse response, string? deviceDisplayName)
    {
        var device = HtmlEncoder.Default.Encode(
            string.IsNullOrWhiteSpace(deviceDisplayName) ? "watch" : deviceDisplayName);

        return Render(response, StatusCodes.Status200OK, $$"""
            <h1>All set — thank you</h1>
            <p class="lede">Your {{device}} is connected.</p>
            <p class="note">
              There's nothing else for you to do, and nothing to install. You can close this page.
            </p>
            """);
    }

    /// <summary>The wearer said it was not them, on our page rather than the provider's.</summary>
    public static ContentResult Declined(HttpResponse response) =>
        Render(response, StatusCodes.Status200OK, """
            <h1>No problem — nothing was shared</h1>
            <p class="lede">We've let them know, and this link won't work any more.</p>
            <p class="note">
              If you think you got this by mistake, you can ignore it. You can close this page.
            </p>
            """);

    /// <summary>
    /// The grant did not happen — refused at the provider, or something went wrong — and the
    /// invitation is still live, so it is worth offering another go.
    /// </summary>
    public static ContentResult NotGranted(HttpResponse response, string? token)
    {
        var retry = token is null
            ? string.Empty
            : $"""
               <form method="get" action="/connect">
                 <input type="hidden" name="t" value="{HtmlEncoder.Default.Encode(token)}">
                 <button class="cta" type="submit">Try again</button>
               </form>
               """;

        return Render(response, StatusCodes.Status200OK, $$"""
            <h1>Nothing was shared</h1>
            <p class="lede">The connection wasn't completed, so no data has been shared.</p>
            {{retry}}
            <p class="note">
              If you'd rather not, just close this page — nothing happens either way.
            </p>
            """);
    }

    /// <summary>
    /// The one ending for every link that cannot be used: unknown, expired, already used, declined
    /// or withdrawn.
    /// </summary>
    /// <remarks>
    /// Identical copy and an identical status for all five, on purpose. A page that said "expired"
    /// for one and "not found" for another would answer, for any token an attacker cared to try,
    /// whether it had ever been a real invitation — and 404 versus 410 would answer it just as
    /// loudly as the words would.
    /// </remarks>
    public static ContentResult NotAvailable(HttpResponse response) =>
        Render(response, StatusCodes.Status404NotFound, """
            <h1>This link isn't available</h1>
            <p class="lede">It may have already been used, or it may have expired.</p>
            <p class="note">
              Ask whoever sent it to you for a new one. You can close this page.
            </p>
            """);

    private static ContentResult Render(HttpResponse response, int statusCode, string body)
    {
        // The URL carries a live invitation token: keep it out of caches, out of the Referer header
        // of anything this page links to, and out of a frame on somebody else's site.
        response.Headers.CacheControl = "no-store";
        response.Headers.Pragma = "no-cache";
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers.XFrameOptions = "DENY";
        response.Headers.ContentSecurityPolicy = ContentSecurityPolicy;

        return new ContentResult
        {
            StatusCode = statusCode,
            ContentType = MediaTypeNames.Text.Html,
            Content = $$"""
                <!doctype html>
                <html lang="en">
                <head>
                <meta charset="utf-8">
                <meta name="viewport" content="width=device-width, initial-scale=1">
                <meta name="robots" content="noindex, nofollow">
                <title>CardiTrack</title>
                <style>
                  :root { color-scheme: light dark; }
                  * { box-sizing: border-box; }
                  body {
                    margin: 0; min-height: 100vh; display: flex; align-items: center; justify-content: center;
                    padding: 2rem 1rem;
                    font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif;
                    background: #f7f9fc; color: #17233a; line-height: 1.5;
                  }
                  main { max-width: 32rem; width: 100%; }
                  h1 { font-size: 1.4rem; margin: 0 0 .75rem; }
                  h2 { font-size: 1rem; margin: 1.75rem 0 .5rem; }
                  p { margin: 0 0 .75rem; }
                  .lede { font-size: 1.05rem; }
                  .note { color: #5b6b84; }
                  .small { font-size: .875rem; }
                  a { color: #135497; }
                  ul.shares { margin: 0; padding-left: 1.1rem; }
                  ul.shares li { margin-bottom: .4rem; }
                  form { margin: 0; }
                  button {
                    display: block; width: 100%; margin-top: .75rem; padding: .85rem 1.5rem;
                    border-radius: 999px; border: 0; font: inherit; font-weight: 600; cursor: pointer;
                  }
                  .cta { background: #135497; color: #fff; }
                  .secondary { background: transparent; color: #135497; border: 1px solid #c3cfe0; }
                  @media (prefers-color-scheme: dark) {
                    body { background: #10161f; color: #e8edf5; }
                    .note { color: #97a3b6; }
                    a { color: #7fb2ea; }
                    .secondary { color: #7fb2ea; border-color: #33405a; }
                  }
                </style>
                </head>
                <body><main>
                {{body}}
                </main></body>
                </html>
                """
        };
    }
}
