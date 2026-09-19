using CardiTrack.API.Infrastructure.UserContext;
using CardiTrack.Shared.Http;

namespace CardiTrack.API.Infrastructure.Logging;

/// <summary>
/// Who a request was for, and which install it came from, on the log lines written as the request
/// leaves: the Serilog request-completion line and anything <c>ExceptionHandlingMiddleware</c>
/// writes about a fault.
///
/// Those are the two lines a production question starts from, and until this they named neither.
/// A run of 500s on <c>POST /api/v1/notifications/devices</c> could be seen but not attributed —
/// not to a caregiver, not even to a count of distinct devices — so "how many people is this
/// happening to" had no answer from telemetry at all.
/// </summary>
/// <remarks>
/// Read at the point of writing rather than pushed with <c>LogContext</c>, which is what
/// <see cref="CardiTrack.API.Middleware.ClientVersionMiddleware"/> does for the client build. It has to be:
/// Serilog's <c>LogContext</c> is an <c>AsyncLocal</c>, and both writers here sit *outside*
/// <c>UserContextMiddleware</c> in the pipeline, so a property pushed there never reaches them —
/// a change made in a deeper async frame does not flow back to its caller. The scoped
/// <see cref="IUserContext"/> does, because it is one object the whole request shares.
/// </remarks>
public static class RequestIdentityLog
{
    /// <summary>
    /// The queryable names, PascalCase like the enrichers already on every event ("Application",
    /// "Version", "ClientVersion"). Dashboards and log searches are written against these, so a
    /// rename is a breaking change to whatever is watching.
    /// </summary>
    public const string UserIdProperty = "UserId";

    /// <inheritdoc cref="UserIdProperty"/>
    public const string DeviceIdProperty = "DeviceId";

    /// <summary>
    /// Records which install this request is about, for the lines written on the way out. Only
    /// the endpoints that take a device id call this — it arrives in the body, not on every
    /// request, and inventing a header for it would put a second identifier on every call the app
    /// makes to carry it.
    /// </summary>
    /// <returns>
    /// The value as recorded, or null when it was not safe to record. Callers that also want it
    /// in a message of their own should log what comes back rather than the raw request value.
    /// </returns>
    public static string? RecordDeviceId(HttpContext context, string? deviceId)
    {
        // Client-supplied and bound for log lines, so the same gate the client headers pass.
        // Ours is a 32-character hex GUID, comfortably inside it. A value that fails — over-long,
        // or carrying the whitespace and control characters that could forge a second log line —
        // is recorded as no value rather than trimmed into something that reads real.
        if (!ClientHeaderValues.IsValid(deviceId))
            return null;

        context.Items[DeviceIdProperty] = deviceId;
        return deviceId;
    }

    /// <summary>
    /// The caller, or null before <c>UserContextMiddleware</c> has resolved one — an
    /// unauthenticated request, or an authenticated one still mid-onboarding, where the database
    /// identity does not exist yet.
    /// </summary>
    public static Guid? UserId(HttpContext context)
    {
        var userContext = context.RequestServices?.GetService<IUserContext>();
        return userContext is { UserId: var id } && id != Guid.Empty ? id : null;
    }

    /// <summary>The install recorded by <see cref="RecordDeviceId"/>, if this endpoint recorded one.</summary>
    public static string? DeviceId(HttpContext context) =>
        context.Items.TryGetValue(DeviceIdProperty, out var deviceId) ? deviceId as string : null;

    /// <summary>
    /// The same two properties as a logging scope, for a writer that logs through
    /// <see cref="ILogger"/> rather than Serilog's diagnostic context. Empty when neither is
    /// known, which <c>BeginScope</c> accepts and adds nothing for.
    /// </summary>
    public static Dictionary<string, object> Scope(HttpContext context)
    {
        var scope = new Dictionary<string, object>(capacity: 2);

        if (UserId(context) is { } userId)
            scope[UserIdProperty] = userId;

        if (DeviceId(context) is { } deviceId)
            scope[DeviceIdProperty] = deviceId;

        return scope;
    }
}
