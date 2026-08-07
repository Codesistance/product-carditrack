namespace CardiTrack.API.Infrastructure.Auditing;

/// <summary>
/// Marks a controller or action whose responses can carry CardiMember health data, so
/// <see cref="Middleware.AuditLoggingMiddleware"/> records the access.
/// </summary>
/// <remarks>
/// Opt-in by attribute rather than by matching URL prefixes in the middleware. A path list
/// silently stops covering an endpoint the moment someone moves a route, and the failure is
/// invisible — the audit trail just quietly has a hole in it. An attribute travels with the
/// code it protects and shows up in review.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true)]
public sealed class AuditHealthDataAccessAttribute : Attribute
{
    /// <param name="action">
    /// Short verb recorded in <c>AuditLog.Action</c>, e.g. "ViewDashboard". Defaults to the
    /// HTTP method plus route when omitted.
    /// </param>
    public AuditHealthDataAccessAttribute(string? action = null)
    {
        Action = action;
    }

    public string? Action { get; }

    /// <summary>
    /// The kind of record reached, recorded in <c>AuditLog.EntityType</c>. Defaults to
    /// CardiMember since that is what almost every health-data surface resolves to.
    /// </summary>
    public string EntityType { get; init; } = "CardiMember";
}
