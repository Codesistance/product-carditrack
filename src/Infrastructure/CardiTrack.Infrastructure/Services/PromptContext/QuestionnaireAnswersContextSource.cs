using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Security;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Security;

namespace CardiTrack.Infrastructure.Services.PromptContext;

/// <summary>
/// What the family has told us about the member, in answer to questions the service asked.
/// </summary>
/// <remarks>
/// The other end of the questionnaire loop: the digest proposes a question, a caregiver answers it,
/// and the answer comes back here to inform every later generation. Without this the questions
/// would be a survey — the point is that the next summary is written by a model that knows the
/// answer.
/// <para>
/// Excluded from the hero status line, which is one sentence under twelve words: there is nothing
/// it could do with this that would fit, and it is generated on every dashboard view. Adding it
/// later is one flag.
/// </para>
/// </remarks>
internal sealed class QuestionnaireAnswersContextSource : IMemberContextSource
{
    /// <summary>
    /// How many answers travel. The newest few are the ones still describing the member's current
    /// life; an answer from months ago is more likely to mislead than inform, and every line here
    /// competes with the readings for the model's attention.
    /// </summary>
    private const int MaxAnswers = 3;

    /// <summary>Per-answer cap. A caregiver writing at length is answering more than was asked.</summary>
    private const int MaxAnswerLength = 300;

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

        var answered = questionnaires
            .Where(q => q.Status == QuestionnaireStatus.Answered && !string.IsNullOrWhiteSpace(q.AnswerText))
            .Take(MaxAnswers)
            .ToList();

        if (answered.Count == 0)
            return null;

        var lines = answered.Select(q =>
        {
            var question = MedicalPromptBlocks.Flatten(
                EncryptedFieldReader.Reveal(_encryption, q.QuestionText) ?? string.Empty);
            var answer = MedicalPromptBlocks.Flatten(
                EncryptedFieldReader.Reveal(_encryption, q.AnswerText) ?? string.Empty);

            if (answer.Length > MaxAnswerLength)
                answer = $"{answer[..MaxAnswerLength]}…";

            return $"- Q: {question} A: {answer}";
        });

        return new MemberContextSection("Family answers to earlier questions", string.Join("\n", lines));
    }
}
