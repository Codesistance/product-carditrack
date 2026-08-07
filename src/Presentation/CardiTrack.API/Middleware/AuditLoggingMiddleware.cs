using CardiTrack.API.Infrastructure.Auditing;
using CardiTrack.API.Infrastructure.UserContext;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Entities;

namespace CardiTrack.API.Middleware;

/// <summary>
/// Records who reached whose health data, for endpoints marked
/// <see cref="AuditHealthDataAccessAttribute"/>.
/// </summary>
/// <remarks>
/// Request-scoped rather than a SaveChanges interceptor: the great majority of health-data
/// access is a read, and a read produces no change for an interceptor to see. An interceptor
/// would record edits and miss every dashboard view — the opposite of what accountability needs.
/// </remarks>
public class AuditLoggingMiddleware
{
    /// <summary>Route values that name the member whose data is being reached.</summary>
    private static readonly string[] CardiMemberRouteKeys = ["cardiMemberId", "memberId", "id"];

    // Mirror AuditLogConfiguration. Every one of these is bounded in the database, and an
    // overflow fails the whole write — losing the record entirely rather than shortening it.
    // The default Action is method + path, so a route carrying several GUIDs passes 100 easily.
    private const int MaxActionLength = 100;
    private const int MaxEntityTypeLength = 100;
    private const int MaxIpAddressLength = 50;
    private const int MaxUserAgentLength = 500;
    private const int MaxRequestPathLength = 500;
    private const int MaxHttpMethodLength = 10;

    private readonly RequestDelegate _next;
    private readonly ILogger<AuditLoggingMiddleware> _logger;

    public AuditLoggingMiddleware(RequestDelegate next, ILogger<AuditLoggingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(
        HttpContext httpContext, IUserContext userContext, IAuditLogRepository auditLogs)
    {
        // The response status is part of the record — a denied attempt is as worth keeping as a
        // successful one — so the entry is written after the pipeline returns.
        await _next(httpContext);

        var descriptor = httpContext.GetEndpoint()?.Metadata.GetMetadata<AuditHealthDataAccessAttribute>();
        if (descriptor is null)
            return;

        // AuditLog.UserId is required and the record is meaningless without a subject. An
        // unauthenticated caller never got past [Authorize] to real data anyway.
        if (!userContext.IsAuthenticated || userContext.UserId == Guid.Empty)
            return;

        try
        {
            await auditLogs.AppendAsync(new AuditLog
            {
                UserId = userContext.UserId,
                CardiMemberId = ResolveCardiMemberId(httpContext),
                Action = Truncate(
                    descriptor.Action ?? $"{httpContext.Request.Method} {httpContext.Request.Path}",
                    MaxActionLength),
                EntityType = Truncate(descriptor.EntityType, MaxEntityTypeLength),
                Timestamp = DateTime.UtcNow,
                IpAddress = Truncate(
                    httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown", MaxIpAddressLength),
                UserAgent = Truncate(httpContext.Request.Headers.UserAgent.ToString(), MaxUserAgentLength),
                // Path only. The query string carries OAuth codes on some routes, and this
                // table is read by more people than the logs are.
                RequestPath = Truncate(httpContext.Request.Path.Value ?? string.Empty, MaxRequestPathLength),
                HttpMethod = Truncate(httpContext.Request.Method, MaxHttpMethodLength),
                ResponseStatus = httpContext.Response.StatusCode,
            }, httpContext.RequestAborted);
        }
        catch (Exception ex)
        {
            // The response has already gone out; throwing here would turn a successful request
            // into a 500 after the fact. Losing an audit entry is bad, so it is logged loudly
            // rather than swallowed — but it must not take the request with it.
            _logger.LogError(ex,
                "Failed to write audit entry for {Method} {Path} by user {UserId}",
                httpContext.Request.Method, httpContext.Request.Path, userContext.UserId);
        }
    }

    /// <summary>
    /// Pulls the member id out of the route. Null is legitimate — some audited endpoints take
    /// the member in the body or span several — and the entry is still worth writing without it.
    /// </summary>
    private static Guid? ResolveCardiMemberId(HttpContext httpContext)
    {
        foreach (var key in CardiMemberRouteKeys)
        {
            if (httpContext.Request.RouteValues.TryGetValue(key, out var raw)
                && Guid.TryParse(raw?.ToString(), out var id))
            {
                return id;
            }
        }

        return null;
    }

    /// <summary>Columns are bounded; a long User-Agent must not fail the write.</summary>
    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
