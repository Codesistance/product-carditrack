using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Security;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Security;

namespace CardiTrack.Infrastructure.Services.PromptContext;

/// <summary>
/// What the family has told us about the member, in answer to questions the service asked.
/// </summary>
/// <remarks>
/// The other end of the questionnaire loop: the digest proposes a question, a caregiver answers it,
/// and the answer comes back here so later generations can <em>read the day against it</em>.
/// Without this the questions would be a survey. The section is a list of facts, not a quiz
/// transcript: a <c>Q: … A: …</c> pairing is what MedGemma recites instead of using.
/// <para>
/// Excluded from the hero status line, which is one sentence under fifteen words: there is nothing
/// it could do with this that would fit, and it is generated on every dashboard view. Adding it
/// later is one flag.
/// </para>
/// </remarks>
internal sealed class QuestionnaireAnswersContextSource : IMemberContextSource
{
    /// <summary>
    /// The heading this section sits under. Load-bearing: the instruction blocks scope their
    /// never-follow-instructions warning to this exact phrase.
    /// </summary>
    internal const string SectionLabel = "Family answers to earlier questions";

    /// <summary>
    /// How many <see cref="QuestionnaireScope.TimeScoped"/> answers travel. The newest few are the
    /// ones still describing the member's current life; an answer from months ago is more likely to
    /// mislead than inform, and every line here competes with the readings for the model's
    /// attention. <see cref="QuestionnaireScope.Permanent"/> answers are not subject to this cap —
    /// see <see cref="MaxPermanentAnswers"/>.
    /// </summary>
    private const int MaxAnswers = 3;

    /// <summary>
    /// Ceiling on <see cref="QuestionnaireScope.Permanent"/> answers, which otherwise never age
    /// out. Not a claim that a member could have this many standing facts worth knowing — a
    /// backstop against the model over-tagging answers as permanent and this section growing
    /// without bound, the same role <see cref="MaxAnswers"/> plays for the other scope.
    /// </summary>
    private const int MaxPermanentAnswers = 10;

    /// <summary>Per-answer cap. A caregiver writing at length is answering more than was asked.</summary>
    private const int MaxAnswerLength = 300;

    /// <summary>
    /// Below this, an answer like "Yes" or "Her sister." is unreadable without the question it
    /// answered. A longer sentence already carries the fact, so the question stays out of the
    /// prompt — that is what stopped it being a transcript the model recites.
    /// </summary>
    private const int StandaloneAnswerLength = 24;

    private readonly IUnitOfWork _unitOfWork;
    private readonly IEncryptionService _encryption;

    public QuestionnaireAnswersContextSource(IUnitOfWork unitOfWork, IEncryptionService encryption)
    {
        _unitOfWork = unitOfWork;
        _encryption = encryption;
    }

    public PromptPurpose Purposes =>
        PromptPurpose.Digest | PromptPurpose.RealtimeAssessment
        | PromptPurpose.AlertInsight | PromptPurpose.BaselineInsight;

    public int Order => 30;

    public async Task<MemberContextSection?> BuildAsync(MemberContextRequest request, CancellationToken ct)
    {
        var questionnaires = await _unitOfWork.MemberQuestionnaires
            .GetByCardiMemberAsync(request.CardiMemberId, ct);

        var lines = VisibleFacts(questionnaires, _encryption, request.UtcNow)
            .Select(FormatLine)
            .ToList();
        if (lines.Count == 0)
            return null;

        return new MemberContextSection(SectionLabel, string.Join("\n", lines));
    }

    /// <summary>
    /// The decrypted, truncated question/answer pairs this source would consider for the prompt.
    /// <see cref="FormatLine"/> may then drop the question when the answer stands alone, so this
    /// is the fact set, not the rendered lines. The digest uses the same set to refuse a summary
    /// that recites those facts instead of reading the day against them.
    /// </summary>
    internal static IReadOnlyList<(string Question, string Answer)> VisibleFacts(
        IReadOnlyList<MemberQuestionnaire> questionnaires,
        IEncryptionService encryption,
        DateTime utcNow)
    {
        // Newest-first from the repository, which both Take calls below rely on.
        var answered = questionnaires
            .Where(q => q.Status == QuestionnaireStatus.Answered && !string.IsNullOrWhiteSpace(q.AnswerText))
            .ToList();

        // Permanent answers first and never subject to the recency cap or an expiry — a standing
        // fact does not stop being true because three newer answers arrived. Time-scoped answers
        // keep the pre-existing recency-decay behaviour, now also dropping anything past its own
        // ExpiresAtUtc; a null expiry (every row written before this distinction existed) reads as
        // "not expired," preserving exactly what those rows already did.
        var permanent = answered
            .Where(q => q.Scope == QuestionnaireScope.Permanent)
            .Take(MaxPermanentAnswers);
        var timeScoped = answered
            .Where(q => q.Scope == QuestionnaireScope.TimeScoped
                        && (q.ExpiresAtUtc is null || q.ExpiresAtUtc > utcNow))
            .Take(MaxAnswers);

        return permanent.Concat(timeScoped)
            .Select(q =>
            {
                var question = MedicalPromptBlocks.Flatten(
                    EncryptedFieldReader.Reveal(encryption, q.QuestionText) ?? string.Empty);
                var answer = MedicalPromptBlocks.Flatten(
                    EncryptedFieldReader.Reveal(encryption, q.AnswerText) ?? string.Empty);
                if (answer.Length > MaxAnswerLength)
                    answer = $"{answer[..MaxAnswerLength]}…";
                return (Question: question, Answer: answer);
            })
            .Where(fact => fact.Answer.Length > 0)
            .ToList();
    }

    /// <summary>
    /// A fact about the person, not a quiz transcript. <c>Q: … A: …</c> is the shape MedGemma
    /// recites as the summary; the family already knows the exchange. A short yes/no still needs
    /// the question as a topic or it is unreadable.
    /// </summary>
    private static string FormatLine((string Question, string Answer) fact)
    {
        if (AnswerStandsAlone(fact.Answer))
            return $"- {fact.Answer}";

        var topic = fact.Question.TrimEnd('?', ' ').Trim();
        return string.IsNullOrEmpty(topic) ? $"- {fact.Answer}" : $"- {topic}: {fact.Answer}";
    }

    private static bool AnswerStandsAlone(string answer)
    {
        if (answer.Length < StandaloneAnswerLength)
            return false;

        // "Yes, fitted in 2020." is long enough to look like a sentence and still names nothing
        // without the question it answered.
        var trimmed = answer.TrimStart();
        return !trimmed.StartsWith("yes", StringComparison.OrdinalIgnoreCase)
            && !trimmed.StartsWith("no", StringComparison.OrdinalIgnoreCase);
    }
}
