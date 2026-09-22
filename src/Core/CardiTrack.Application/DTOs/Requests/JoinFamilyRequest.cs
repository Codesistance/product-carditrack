namespace CardiTrack.Application.DTOs.Requests;

/// <summary>Asks to join a family (POST /api/v1/families/join-requests).</summary>
public class JoinFamilyRequest
{
    /// <summary>
    /// The Family ID, as typed or as auto-filled by a link. Separators, spaces and case are
    /// forgiven — a code read out over a bad line should find the same family as one pasted.
    /// </summary>
    public string FamilyId { get; set; } = string.Empty;
}
