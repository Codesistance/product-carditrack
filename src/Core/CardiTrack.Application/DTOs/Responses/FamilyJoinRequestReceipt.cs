namespace CardiTrack.Application.DTOs.Responses;

/// <summary>
/// What somebody gets back for asking to join a family.
/// </summary>
/// <remarks>
/// Deliberately says almost nothing. No family name, no member names, no confirmation that the
/// code named a real family at all — a search through the code space has to return the same shape
/// whatever it hits, or the receipt becomes an oracle for which families exist.
/// <para>
/// <see cref="RequestId"/> is null when nothing was recorded, which happens both for an unknown
/// code and for an asker who is already in the family. The caller cannot tell those apart, and
/// that is the point.
/// </para>
/// </remarks>
/// <param name="RequestId">The request, when one was created.</param>
/// <param name="ExpiresAt">When it stops being answerable, when one was created.</param>
public record FamilyJoinRequestReceipt(Guid? RequestId, DateTime? ExpiresAt);
