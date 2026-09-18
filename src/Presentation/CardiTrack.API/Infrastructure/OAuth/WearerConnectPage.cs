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
    /// <remarks>
    /// <para>
    /// <strong><c>form-action</c> has to name the provider, not just this host.</strong> Browsers
    /// enforce the directive across the submission's <em>redirect chain</em>, and "Continue" posts
    /// to us and is immediately redirected to the provider's consent screen. With only our own
    /// origin listed the submission is refused — and refused silently, with the message naming our
    /// endpoint rather than the redirect, which is what made this look like a broken button rather
    /// than a policy. Reported from a real device on 2026-09-18, twice: once against
    /// <c>'self'</c> alone, then again against <c>'self'</c> plus this origin.
    /// </para>
    /// <para>
    /// The provider origins come from the configured <c>DeviceProviders</c> authorization URLs, so
    /// a new provider is permitted the moment it is configured and nothing here has to be
    /// remembered. The directive still names an exact, short list: this host, and the consent
    /// screens we deliberately send people to.
    /// </para>
    /// </remarks>
    private static string ContentSecurityPolicyFor(HttpRequest request, IEnumerable<string> providerOrigins)
    {
        var origins = new List<string> { $"{request.Scheme}://{request.Host.Value}" };
        origins.AddRange(providerOrigins);

        return "default-src 'none'; style-src 'unsafe-inline'; " +
               $"form-action 'self' {string.Join(' ', origins.Distinct())}; " +
               "base-uri 'none'; frame-ancestors 'none'";
    }

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
        HttpResponse response,
        WearerInviteView invite,
        string token,
        string privacyPolicyUrl,
        IEnumerable<string> providerOrigins)
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

        return Render(response, StatusCodes.Status200OK, providerOrigins: providerOrigins, body: $$"""
            <h1>{{caregiver}} would like to see your health data</h1>
            <p class="lede">
              To look after <strong>{{member}}</strong> using your {{device}}.
            </p>

            <h2>This will let CardiTrack see</h2>
            <ul class="shares">
              <li>
                <span class="tick" aria-hidden="true">&#x2713;</span>
                <span>
                  <span class="what">Your heart rate</span>
                  <span class="why">So it can spot if something looks off</span>
                </span>
              </li>
              <li>
                <span class="tick" aria-hidden="true">&#x2713;</span>
                <span>
                  <span class="what">Your activity and steps</span>
                  <span class="why">To see you're keeping active</span>
                </span>
              </li>
              <li>
                <span class="tick" aria-hidden="true">&#x2713;</span>
                <span>
                  <span class="what">Your sleep</span>
                  <span class="why">How long and how well you rested</span>
                </span>
              </li>
              <li>
                <span class="tick" aria-hidden="true">&#x2713;</span>
                <span>
                  <span class="what">Your watch's battery</span>
                  <span class="why">So nobody is caught out by a flat watch</span>
                </span>
              </li>
            </ul>

            <div class="divider"></div>

            <p class="note">
              {{caregiver}} sees these. CardiTrack never posts anything to your accounts and never
              sells your data. You can stop sharing at any time from {{revokeLink}}, and ask
              {{caregiver}} to remove the device.
            </p>
            <p class="note">
              You'll sign in with the account your {{device}} already uses — CardiTrack never sees
              your password. <a href="{{privacy}}" rel="noopener noreferrer">Privacy policy</a>
            </p>

            <div class="actions">
              <form method="post" action="/connect/decline">
                <input type="hidden" name="t" value="{{safeToken}}">
                <button class="secondary" type="submit">This isn't me</button>
              </form>
              <form method="post" action="/connect/start">
                <input type="hidden" name="t" value="{{safeToken}}">
                <button class="cta" type="submit">Continue</button>
              </form>
            </div>
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

    private static ContentResult Render(
        HttpResponse response, int statusCode, string body, IEnumerable<string>? providerOrigins = null)
    {
        // The URL carries a live invitation token: keep it out of caches, out of the Referer header
        // of anything this page links to, and out of a frame on somebody else's site.
        response.Headers.CacheControl = "no-store";
        response.Headers.Pragma = "no-cache";
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers.XFrameOptions = "DENY";
        response.Headers.ContentSecurityPolicy =
            ContentSecurityPolicyFor(response.HttpContext.Request, providerOrigins ?? []);

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
                  :root {
                    color-scheme: light dark;
                    --ink: #202124;
                    --muted: #5f6368;
                    --line: #dadce0;
                    --card: #ffffff;
                    --page: #ffffff;
                    --accent: #1a73e8;
                    --accent-ink: #ffffff;
                    --chip: #f1f3f4;
                  }
                  * { box-sizing: border-box; }
                  body {
                    margin: 0; min-height: 100vh;
                    display: flex; align-items: center; justify-content: center;
                    padding: 24px 16px;
                    font-family: "Google Sans", Roboto, -apple-system, BlinkMacSystemFont, "Segoe UI", Arial, sans-serif;
                    background: var(--page); color: var(--ink);
                    line-height: 1.5; -webkit-font-smoothing: antialiased;
                  }
                  main {
                    width: 100%; max-width: 448px;
                    background: var(--card);
                    border: 1px solid var(--line);
                    border-radius: 28px;
                    padding: 48px 40px 32px;
                  }
                  .brand {
                    display: flex; align-items: center; gap: 8px;
                    font-size: 15px; color: var(--muted); margin-bottom: 20px;
                  }
                  .mark {
                    width: 22px; height: 22px; border-radius: 6px; flex: 0 0 22px;
                    background: linear-gradient(135deg, #135497, #2bb673);
                  }
                  h1 {
                    font-size: 24px; font-weight: 400; line-height: 1.3;
                    margin: 0 0 12px; letter-spacing: 0;
                  }
                  .lede { font-size: 14px; color: var(--muted); margin: 0 0 8px; }
                  h2 {
                    font-size: 14px; font-weight: 500; color: var(--ink);
                    margin: 28px 0 8px;
                  }
                  p { margin: 0 0 12px; font-size: 14px; }
                  .note { color: var(--muted); font-size: 12px; line-height: 1.6; }
                  a { color: var(--accent); text-decoration: none; }
                  a:hover { text-decoration: underline; }

                  ul.shares { list-style: none; margin: 0; padding: 0; }
                  ul.shares li {
                    display: flex; gap: 14px; align-items: flex-start;
                    padding: 10px 0; font-size: 14px;
                  }
                  ul.shares .tick {
                    flex: 0 0 20px; width: 20px; height: 20px; margin-top: 1px;
                    border-radius: 50%; background: var(--chip);
                    display: flex; align-items: center; justify-content: center;
                    color: var(--muted); font-size: 12px;
                  }
                  ul.shares .what { display: block; }
                  ul.shares .why { display: block; color: var(--muted); font-size: 13px; }

                  .divider { height: 1px; background: var(--line); margin: 24px 0 20px; }

                  .actions {
                    display: flex; justify-content: flex-end; align-items: center;
                    gap: 8px; margin-top: 28px; flex-wrap: wrap;
                  }
                  form { margin: 0; }
                  button {
                    font: inherit; font-size: 14px; font-weight: 500; cursor: pointer;
                    border-radius: 100px; padding: 10px 24px; border: 1px solid transparent;
                    min-height: 40px;
                  }
                  .cta { background: var(--accent); color: var(--accent-ink); }
                  .cta:hover { filter: brightness(1.07); }
                  .secondary { background: transparent; color: var(--accent); }
                  .secondary:hover { background: rgba(26,115,232,.08); }

                  /* One column, full-width buttons on a phone: the side-by-side pair is a
                     desktop shape, and a 24px-padded row of two is a mis-tap waiting to happen. */
                  @media (max-width: 420px) {
                    main { padding: 32px 24px 24px; border: 0; border-radius: 0; }
                    .actions { flex-direction: column-reverse; align-items: stretch; gap: 10px; }
                    button { width: 100%; }
                  }

                  @media (prefers-color-scheme: dark) {
                    :root {
                      --ink: #e8eaed; --muted: #9aa0a6; --line: #5f6368;
                      --card: #1f1f1f; --page: #131314; --accent: #8ab4f8;
                      --accent-ink: #202124; --chip: #2d2e30;
                    }
                  }
                </style>
                </head>
                <body><main>
                <div class="brand"><span class="mark"></span>CardiTrack</div>
                {{body}}
                </main></body>
                </html>
                """
        };
    }
}
