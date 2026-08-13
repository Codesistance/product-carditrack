using System.Diagnostics;
using CardiTrack.Shared.Http;
using Serilog.Context;

namespace CardiTrack.API.Middleware;

/// <summary>
/// Records which client build made the request, from the headers the mobile app sends
/// (<see cref="ClientHeaderNames"/>), onto both of the request's telemetry channels:
/// tags on the ambient server <see cref="Activity"/>, which export with the span, and Serilog
/// <see cref="LogContext"/> properties, which land on every log line written while the request is
/// in flight. Without this the API can see that a call was slow or threw, but not which build made
/// it — so a regression can't be pinned to a release, and Android-only breakage can't be told from
/// iOS breakage at all.
///
/// Registered ahead of UseSerilogRequestLogging on purpose. That middleware writes its
/// request-completion event on the way back out, i.e. inside the scope pushed here, so the
/// per-request summary line carries the properties too — which is the line most queries start from.
///
/// Setting the Activity tags here rather than in an OpenTelemetry EnrichWithHttpRequest callback is
/// also deliberate. ASP.NET Core has already created the server Activity by the time middleware
/// runs, so there is nothing to wait for; and AddApmTracing is a no-op whenever APM is
/// unconfigured (every local dev machine), which would have taken the log properties down with the
/// span tags.
/// </summary>
public sealed class ClientVersionMiddleware
{
    /// <summary>
    /// Serilog property names, PascalCase to match the enrichers already on every event
    /// ("Application", "Version", "Auth0UserId").
    ///
    /// Public because these four strings are the queryable contract, not an implementation
    /// detail: dashboards, monitors and log searches are written against them, so a rename here
    /// is a breaking change to whatever is watching, and anything referencing them should do so
    /// by name rather than by retyping the literal.
    /// </summary>
    public const string VersionProperty = "ClientVersion";
    public const string PlatformProperty = "ClientPlatform";

    /// <summary>
    /// Dotted, matching OpenTelemetry attribute style (and the "client.*" namespace, which
    /// describes the caller of a service). Semantic conventions define no version attribute of
    /// their own for this, so these are ours — but named where a reader would look for them.
    /// </summary>
    public const string VersionTag = "client.version";
    public const string PlatformTag = "client.platform";

    private readonly RequestDelegate _next;

    public ClientVersionMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        var version = Read(context, ClientHeaderNames.ClientVersion);
        var platform = Read(context, ClientHeaderNames.ClientPlatform);

        // The Web app and every server-to-server caller send neither, and health probes are the
        // highest-volume traffic there is — none of them should pay for a LogContext push.
        if (version is null && platform is null)
        {
            await _next(context);
            return;
        }

        var activity = Activity.Current;
        if (version is not null)
            activity?.SetTag(VersionTag, version);
        if (platform is not null)
            activity?.SetTag(PlatformTag, platform);

        // Two properties rather than one combined string: they are filtered on independently
        // ("everything on 1.4.2" versus "everything on android"). Disposal order is the reverse of
        // declaration, which is what LogContext's stack requires.
        using IDisposable? versionScope =
            version is null ? null : LogContext.PushProperty(VersionProperty, version);
        using IDisposable? platformScope =
            platform is null ? null : LogContext.PushProperty(PlatformProperty, platform);

        await _next(context);
    }

    /// <summary>
    /// Reads one header, keeping it only if it is safe to record. These arrive unauthenticated on
    /// a public API and flow straight into log lines and span tags, so an unvalidated value would
    /// be a log-forging and tag-cardinality hole — <see cref="ClientHeaderValues.IsValid"/> is the
    /// gate, shared with the client that produces them.
    /// </summary>
    private static string? Read(HttpContext context, string headerName)
    {
        if (!context.Request.Headers.TryGetValue(headerName, out var values))
            return null;

        // A repeated header arrives as several values with no way to tell which is meant; treating
        // that as absent is the only answer that can't be steered by whoever sent the duplicate.
        var value = values.Count == 1 ? values[0] : null;
        return ClientHeaderValues.IsValid(value) ? value : null;
    }
}
