using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.DTOs.Common;

/// <summary>
/// A resolved alert-list query: the member scope has already been narrowed to what the
/// requesting user may read, and the wire-level strings have already been parsed to enums.
/// </summary>
/// <remarks>
/// Passed to the repository rather than assembled there, so the access decision stays in the
/// service layer and the repository never has to know who is asking.
/// </remarks>
public sealed record AlertQuery(
    IReadOnlyCollection<Guid> CardiMemberIds,
    AlertSeverity? Severity = null,
    AlertStatus? Status = null,
    DateTime? From = null,
    DateTime? To = null,
    int Limit = AlertQuery.DefaultLimit,
    int Offset = 0)
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;
}
