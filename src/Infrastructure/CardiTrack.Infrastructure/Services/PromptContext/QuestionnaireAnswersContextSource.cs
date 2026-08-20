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
        | PromptPurpose.AlertInsight | PromptPurpose.BaselineInsight | PromptPurpose.Daybook
        | PromptPurpose.Weekbook | PromptPurpose.Monthbook | PromptPurpose.MemberChat;

    public int Order => 30;

    public async Task<MemberContextSection?> BuildAsync(MemberContextRequest request, CancellationToken ct)
    {
        var questionnaires = await _unitOfWork.MemberQuestionnaires
            .GetByCardiMemberAsync(request.CardiMemberId, ct);

        var lines = VisibleFacts(questionnaires, _encryption, request.UtcNow)
            .Select(fact => FormatLine(fact, request.UtcNow))
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
    internal static IReadOnlyList<FamilyFact> VisibleFacts(
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
                return new FamilyFact(question, answer, q.Scope, q.AnsweredAtUtc ?? q.GeneratedAtUtc);
            })
            .Where(fact => fact.Answer.Length > 0)
            .ToList();
    }

    /// <summary>
    /// One thing the family has told us, with what the prompt needs to date it.
    /// </summary>
    /// <param name="Scope">
    /// Whether this is a standing fact or something that was only true around when it was given —
    /// see <see cref="FormatLine"/> for why only one of those two gets a date.
    /// </param>
    /// <param name="ToldAtUtc">
    /// When the family gave this answer. Falls back to when the question was asked, for the rows
    /// that predate <see cref="MemberQuestionnaire.AnsweredAtUtc"/> being stamped on every save.
    /// </param>
    internal sealed record FamilyFact(
        string Question, string Answer, QuestionnaireScope Scope, DateTime ToldAtUtc);

    /// <summary>
    /// A fact about the person, not a quiz transcript. <c>Q: … A: …</c> is the shape MedGemma
    /// recites as the summary; the family already knows the exchange. A short yes/no still needs
    /// the question as a topic or it is unreadable.
    /// </summary>
    /// <remarks>
    /// A <see cref="QuestionnaireScope.TimeScoped"/> answer is dated, because undated it reads as
    /// current and is not. "Did he feel tired at all today?" answered "he had a busy day with
    /// chores" describes the day it was asked about; the next morning's summary put that same
    /// sentence in front of a caregiver as the explanation for a day the member had barely started,
    /// and would have gone on doing so for the full thirty days these answers inform prompts for.
    /// Permanent answers are left undated on purpose — a pacemaker fitted in 2020 is no less true
    /// this morning, and a date beside it invites the model to weigh it as news.
    /// </remarks>
    private static string FormatLine(FamilyFact fact, DateTime utcNow)
    {
        var body = AnswerStandsAlone(fact.Answer)
            ? fact.Answer
            : fact.Question.TrimEnd('?', ' ').Trim() is { Length: > 0 } topic
                ? $"{topic}: {fact.Answer}"
                : fact.Answer;

        return fact.Scope == QuestionnaireScope.Permanent
            ? $"- {body}"
            : $"- (told to us {WhenTold(fact.ToldAtUtc, utcNow)}, about that time and not since) {body}";
    }

    /// <summary>
    /// How long ago the family said this, in the coarse terms a summary should weigh it by. Whole
    /// elapsed days rather than UTC calendar dates: an answer given eleven hours ago may still be
    /// "today" across a midnight the member never lived through, and this section has no member
    /// timezone to label by their civil day. Elapsed days keep "yesterday" meaning roughly a day
    /// old, not "the UTC date flipped while they slept".
    /// </summary>
    private static string WhenTold(DateTime toldAtUtc, DateTime utcNow) =>
        (int)(utcNow - toldAtUtc).TotalDays switch
        {
            <= 0 => "today",
            1 => "yesterday",
            var days and < 7 => $"{days} days ago",
            var days => $"about {days / 7} week{(days / 7 == 1 ? string.Empty : "s")} ago",
        };

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
