using System.ComponentModel.DataAnnotations;

namespace CardiTrack.Application.DTOs.Requests;

/// <summary>
/// The optional body of <c>POST /api/v1/alerts/{id}/acknowledge</c> and
/// <c>POST /api/v1/alerts/{id}/close</c>: what the caregiver did, and optionally a line about it.
/// </summary>
/// <remarks>
/// Every field is optional, and so is the body itself — acknowledging with no body is the shape
/// that already shipped and keeps working. A canned code alone is a complete answer, and so is a
/// note alone: requiring a pick before somebody can type would make the chips a toll rather than
/// a shortcut.
/// </remarks>
public class AlertAnswerRequest
{
    /// <summary>
    /// A code from this alert's <c>responseOptions</c>. Rejected with 400 when the rule does not
    /// offer it, rather than stored — see <c>AlertResponseCatalog</c>.
    /// </summary>
    [MaxLength(64)]
    public string? ResponseCode { get; set; }

    /// <summary>
    /// The caregiver's own words. Capped at 500 characters — long enough for what happened, short
    /// enough that the alert stays a thing the family scans rather than reads.
    /// </summary>
    [MaxLength(500)]
    public string? Note { get; set; }
}
