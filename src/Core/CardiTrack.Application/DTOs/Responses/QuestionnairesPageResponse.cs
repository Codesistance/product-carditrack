namespace CardiTrack.Application.DTOs.Responses;

/// <summary>
/// What the questionnaires screen needs in one call: the question still waiting on an answer (there
/// is at most one, and it is never subject to search or paging — a caregiver must always be able to
/// find it), and a page of the answered history, filtered by search text if any was given.
/// </summary>
public class QuestionnairesPageResponse
{
    /// <summary>Whether this member has ever been asked anything, of any status — drives whether
    /// the caller shows a link through to the archive at all.</summary>
    public bool HasAny { get; set; }

    public QuestionnaireResponse? Pending { get; set; }

    public PagedResult<QuestionnaireResponse> Answered { get; set; } = new();
}
