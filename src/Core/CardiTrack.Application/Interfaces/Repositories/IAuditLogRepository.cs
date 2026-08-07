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
    /// <summary>Writes one entry. Never throws into the request — see the implementation.</summary>
    Task AppendAsync(AuditLog entry, CancellationToken ct = default);

    /// <summary>Entries for one CardiMember, newest first. For subject-access requests.</summary>
    Task<IReadOnlyList<AuditLog>> GetByCardiMemberAsync(
        Guid cardiMemberId, DateTime from, DateTime to, CancellationToken ct = default);

    /// <summary>Entries for one user, newest first. For "what did this account do" questions.</summary>
    Task<IReadOnlyList<AuditLog>> GetByUserAsync(
        Guid userId, DateTime from, DateTime to, CancellationToken ct = default);
}
