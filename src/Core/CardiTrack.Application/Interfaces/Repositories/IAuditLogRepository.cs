using CardiTrack.Domain.Entities;

namespace CardiTrack.Application.Interfaces.Repositories;

/// <summary>
/// Append-only record of who accessed whose health data. Required for GDPR accountability
/// (Art. 5(2), 32) and the basis of any later subject-access or breach investigation.
/// </summary>
/// <remarks>
/// Deliberately narrow. There is no update or delete: an audit trail that the application can
/// rewrite is not evidence of anything. <see cref="IRepository{T}"/> is not inherited for the
/// same reason.
/// </remarks>
public interface IAuditLogRepository
{
    /// <summary>
    /// Writes one entry, independently of any surrounding unit of work.
    /// </summary>
    /// <remarks>
    /// Throws if the write fails — an unreachable database is not something this can paper over,
    /// and a caller that needs the entry to exist must be able to tell that it does not. Callers
    /// on the request path are responsible for deciding what to do about that;
    /// <c>AuditLoggingMiddleware</c> logs and continues, because by the time it runs the response
    /// has already been sent.
    /// </remarks>
    Task AppendAsync(AuditLog entry, CancellationToken ct = default);

    /// <summary>Entries for one CardiMember, newest first. For subject-access requests.</summary>
    Task<IReadOnlyList<AuditLog>> GetByCardiMemberAsync(
        Guid cardiMemberId, DateTime from, DateTime to, CancellationToken ct = default);

    /// <summary>Entries for one user, newest first. For "what did this account do" questions.</summary>
    Task<IReadOnlyList<AuditLog>> GetByUserAsync(
        Guid userId, DateTime from, DateTime to, CancellationToken ct = default);
}
