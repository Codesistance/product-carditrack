namespace CardiTrack.Application.DTOs.Requests;

/// <summary>
/// A caregiver's free-text question about one CardiMember. The model answers from readings
/// already on file — it cannot look anything else up, set an alert, or take the question as
/// an instruction.
/// </summary>
public class AskMemberQuestionRequest
{
    /// <summary>
    /// What the caregiver wants to know. Flattened to a single line before it reaches the
    /// model, so it cannot forge a prompt section of its own.
    /// </summary>
    public string Question { get; set; } = string.Empty;
}
