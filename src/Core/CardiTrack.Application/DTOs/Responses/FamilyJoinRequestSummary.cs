namespace CardiTrack.Application.DTOs.Responses;

/// <summary>
/// One of the caller's own join requests, for the screen that shows what they are waiting on.
/// </summary>
/// <remarks>
/// The family's name appears here and not on the receipt, because by the time this is read the
/// asker already knows which family they asked — they typed the code. What it still withholds is
/// anything about the family's people or members, which are not theirs to see until an admin says
/// so.
/// </remarks>
/// <param name="RequestId">The request.</param>
/// <param name="FamilyName">Which family, for their own reference.</param>
/// <param name="Status">One of <c>pending</c>, <c>approved</c>, <c>declined</c>, <c>withdrawn</c> or <c>expired</c>.</param>
/// <param name="RequestedAt">When they asked.</param>
/// <param name="ExpiresAt">When it stops being answerable.</param>
public record FamilyJoinRequestSummary(
    Guid RequestId,
    string FamilyName,
    string Status,
    DateTime RequestedAt,
    DateTime ExpiresAt);
