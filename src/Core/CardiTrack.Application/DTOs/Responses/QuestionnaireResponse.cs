namespace CardiTrack.Application.DTOs.Responses;

/// <summary>
/// One question the service asked about a member, and the family's answer if they gave one.
/// </summary>
public class QuestionnaireResponse
{
    public Guid Id { get; set; }

    public Guid CardiMemberId { get; set; }

    /// <summary>The question, in plain text — decrypted on the way out.</summary>
    public string QuestionText { get; set; } = string.Empty;

    /// <summary>The answer, in plain text. Null while the question is unanswered.</summary>
    public string? AnswerText { get; set; }

    /// <summary>Why it was asked, for the apps to show beneath it. Null when the model gave no reason.</summary>
    public string? TriggerContext { get; set; }

    /// <summary>pending / answered / dismissed, lowercase — the same string convention as alert severity.</summary>
    public string Status { get; set; } = "pending";

    public DateTime GeneratedAtUtc { get; set; }

    /// <summary>When it was answered, or last edited. Null while unanswered.</summary>
    public DateTime? AnsweredAtUtc { get; set; }

    /// <summary>Which caregiver answered — several may share a member.</summary>
    public Guid? AnsweredByUserId { get; set; }
}
