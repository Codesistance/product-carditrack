namespace CardiTrack.Application.DTOs.Requests;

/// <summary>Answers a question, or replaces an answer already given — the same request either way.</summary>
public class AnswerQuestionnaireRequest
{
    /// <summary>
    /// What the family says. Free text: the question asked about someone's life, and a caregiver
    /// should not have to fit the answer to a form.
    /// </summary>
    public string AnswerText { get; set; } = string.Empty;
}
