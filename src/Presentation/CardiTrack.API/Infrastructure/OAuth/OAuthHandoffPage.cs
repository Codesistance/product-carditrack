using System.Net.Mime;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Mvc;

namespace CardiTrack.API.Infrastructure.OAuth;

/// <summary>
/// The browser-side half of the native OAuth hand-off.
///
/// A bare 302 whose Location is a custom scheme is honoured by Chrome Custom Tabs and
/// ASWebAuthenticationSession but dropped by browsers — and proxies — that only forward
/// http(s). When it is dropped nothing fires the deep link, so the in-app browser is never
/// dismissed and the user is left looking at the provider's consent page with the app still
/// waiting behind it. Performing the scheme navigation from the page, with a tappable
/// fallback, is the portable form and gives us somewhere to say the tab is finished with.
/// </summary>
internal static class OAuthHandoffPage
{
    /// <summary>Sends the browser into the app deep link, falling back to a tappable link.</summary>
    public static ContentResult Handoff(HttpResponse response, string appUri)
    {
        // Both encoders are needed: the same URI is emitted once into markup and once into a
        // JavaScript string literal, and only the query values within it are already
        // percent-encoded by the caller.
        var href = HtmlEncoder.Default.Encode(appUri);
        var literal = JavaScriptEncoder.Default.Encode(appUri);

        return Render(response, StatusCodes.Status200OK, $$"""
            <h1>Taking you back to CardiTrack…</h1>
            <p>You can close this tab once the app reopens.</p>
            <a class="cta" href="{{href}}">Open CardiTrack</a>
            <noscript><p class="note">Tap <strong>Open CardiTrack</strong> to finish connecting.</p></noscript>
            <script>
              try { location.replace("{{literal}}"); } catch (e) { /* the link below is the fallback */ }
            </script>
            """);
    }

    /// <summary>
    /// Terminal page for a callback we cannot route — an unknown, expired or already-spent
    /// state token leaves us with no deep link to send the browser to.
    /// </summary>
    public static ContentResult Failed(HttpResponse response, string message) =>
        Render(response, StatusCodes.Status400BadRequest, $$"""
            <h1>We couldn't finish that connection</h1>
            <p>{{HtmlEncoder.Default.Encode(message)}}</p>
            <p class="note">Head back to CardiTrack and start the connection again. You can close this tab.</p>
            """);

    private static ContentResult Render(HttpResponse response, int statusCode, string body)
    {
        // The URL carries an authorization code: keep it out of caches and out of the
        // Referer header of anything the page ends up linking to.
        response.Headers.CacheControl = "no-store";
        response.Headers.Pragma = "no-cache";
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers.XContentTypeOptions = "nosniff";

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
                <title>CardiTrack</title>
                <style>
                  :root { color-scheme: light dark; }
                  body {
                    margin: 0; min-height: 100vh; display: flex; align-items: center; justify-content: center;
                    padding: 2rem 1.5rem; text-align: center;
                    font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif;
                    background: #f7f9fc; color: #17233a;
                  }
                  h1 { font-size: 1.25rem; margin: 0 0 .5rem; }
                  p { margin: 0 0 .5rem; line-height: 1.5; }
                  .note { color: #5b6b84; }
                  .cta {
                    display: inline-block; margin-top: 1.25rem; padding: .75rem 1.5rem; border-radius: 999px;
                    background: #135497; color: #fff; text-decoration: none; font-weight: 600;
                  }
                  @media (prefers-color-scheme: dark) {
                    body { background: #10161f; color: #e8edf5; }
                    .note { color: #97a3b6; }
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
