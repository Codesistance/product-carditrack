using System.Security.Cryptography;
using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Security;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Services;

/// <inheritdoc cref="IQuestionnaireService"/>
public class QuestionnaireService : IQuestionnaireService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICardiMemberAccessService _access;
    private readonly IEncryptionService _encryption;

    public QuestionnaireService(
        IUnitOfWork unitOfWork, ICardiMemberAccessService access, IEncryptionService encryption)
    {
        _unitOfWork = unitOfWork;
        _access = access;
        _encryption = encryption;
    }

    public async Task<QuestionnairesPageResponse> GetForMemberAsync(
        Guid requestingUserId,
        Guid cardiMemberId,
        string? search,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        await _access.RequireViewAccessAsync(requestingUserId, cardiMemberId, ct);

        // Question and answer text are encrypted at rest (see ToResponse/Reveal below), so there is
        // no SQL-level way to filter or page on them — every row for this member has to be decrypted
        // before search or paging can be applied. That is the same cost the unpaged endpoint always
        // paid; this only changes how much of the result crosses the wire afterwards.
        var all = await _unitOfWork.MemberQuestionnaires.GetByCardiMemberAsync(cardiMemberId, ct);

        var utcNow = DateTime.UtcNow;

        // Never the lapsed one, whatever its status column still says. The sweep that retires those
        // rows runs on a cadence and the phone retires them on sight, but neither is what makes this
        // safe — this is: a question about a day that has ended does not reach a caregiver because
        // no read path will serve it, not because something got there first.
        var pendingRow = all.FirstOrDefault(
            q => q.Status == QuestionnaireStatus.Pending && !q.HasLapsed(utcNow));

        IEnumerable<MemberQuestionnaire> answeredRows = all
            .Where(q => q.Status == QuestionnaireStatus.Answered && StillOnTheList(q, utcNow))
            .OrderByDescending(q => q.AnsweredAtUtc ?? q.GeneratedAtUtc);

        IEnumerable<QuestionnaireResponse> answered = answeredRows.Select(ToResponse);
        if (!string.IsNullOrWhiteSpace(search))
        {
            answered = answered.Where(q =>
                (q.QuestionText?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
                || (q.AnswerText?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        var answeredList = answered.ToList();

        // page/pageSize arrive as int query params, and only pageSize is upper-bounded by the
        // controller — an extreme page number would overflow an int-only (page - 1) * pageSize.
        // Widening to long for the arithmetic, then clamping to the list length before the Skip
        // needs an int back, keeps this correct (and just an empty page) at any page value instead
        // of wrapping into a bogus skip count.
        var skip = (int)Math.Min((long)(page - 1) * pageSize, answeredList.Count);
        var pageItems = answeredList.Skip(skip).Take(pageSize).ToList();
        var hasMore = (long)page * pageSize < answeredList.Count;

        return new QuestionnairesPageResponse
        {
            HasAny = all.Count > 0,
            Pending = pendingRow is null ? null : ToResponse(pendingRow),
            Answered = new PagedResult<QuestionnaireResponse>
            {
                Items = pageItems,
                Page = page,
                PageSize = pageSize,
                TotalCount = answeredList.Count,
                HasMore = hasMore,
            },
        };
    }

    public async Task<QuestionnaireResponse> AnswerAsync(
        Guid requestingUserId, Guid questionnaireId, string answerText, CancellationToken ct = default)
    {
        var questionnaire = await RequireAccessibleAsync(requestingUserId, questionnaireId, ct);

        questionnaire.AnswerText = _encryption.Encrypt(answerText.Trim());
        questionnaire.Status = QuestionnaireStatus.Answered;
        // Stamped on every answer, including an edit: what a caregiver wants to know beside an
        // answer is when it was last true, not when the question first happened to be answered.
        questionnaire.AnsweredAtUtc = DateTime.UtcNow;
        questionnaire.AnsweredByUserId = requestingUserId;

        _unitOfWork.MemberQuestionnaires.Update(questionnaire);
        await _unitOfWork.SaveChangesAsync();

        return ToResponse(questionnaire);
    }

    public async Task<QuestionnaireResponse> DismissAsync(
        Guid requestingUserId, Guid questionnaireId, CancellationToken ct = default)
    {
        var questionnaire = await RequireAccessibleAsync(requestingUserId, questionnaireId, ct);

        questionnaire.Status = QuestionnaireStatus.Dismissed;

        _unitOfWork.MemberQuestionnaires.Update(questionnaire);
        await _unitOfWork.SaveChangesAsync();

        return ToResponse(questionnaire);
    }

    public async Task<QuestionnaireResponse> ExpireAsync(
        Guid requestingUserId, Guid questionnaireId, CancellationToken ct = default)
    {
        var questionnaire = await RequireAccessibleAsync(requestingUserId, questionnaireId, ct);

        // Idempotent, and the server's own clock decides. The apps call this because they noticed a
        // question had outlived its day, but "the client says it has lapsed" is not a fact the
        // server takes on trust — a wrong device clock would otherwise let one caregiver retire a
        // question the rest of the family still has in front of them.
        if (questionnaire.HasLapsed(DateTime.UtcNow))
        {
            questionnaire.Status = QuestionnaireStatus.Expired;
            _unitOfWork.MemberQuestionnaires.Update(questionnaire);
            await _unitOfWork.SaveChangesAsync();
        }

        return ToResponse(questionnaire);
    }

    public async Task DeleteAsync(
        Guid requestingUserId, Guid questionnaireId, CancellationToken ct = default)
    {
        var questionnaire = await RequireAccessibleAsync(requestingUserId, questionnaireId, ct);

        _unitOfWork.MemberQuestionnaires.Remove(questionnaire);
        await _unitOfWork.SaveChangesAsync();
    }

    /// <summary>
    /// The questionnaire, if this caller may see the member it belongs to.
    /// </summary>
    /// <remarks>
    /// "No such questionnaire" and "not your questionnaire" report the same not-found, so the id
    /// space cannot be probed for which questions exist about whom — the same anti-enumeration
    /// stance <c>HealthInsightService.AnalyzeAlertAsync</c> takes for alerts.
    /// </remarks>
    private async Task<MemberQuestionnaire> RequireAccessibleAsync(
        Guid requestingUserId, Guid questionnaireId, CancellationToken ct)
    {
        var questionnaire = await _unitOfWork.MemberQuestionnaires.GetByIdAsync(questionnaireId);
        if (questionnaire is null
            || !await _access.HasViewAccessAsync(requestingUserId, questionnaire.CardiMemberId, ct))
        {
            throw new KeyNotFoundException($"Questionnaire {questionnaireId} not found.");
        }

        return questionnaire;
    }

    /// <summary>
    /// Whether an answered question still belongs on the family's Q&amp;A list. Standing facts
    /// stay until the family deletes them. Momentary ones drop off once they expire — the same
    /// clock that stops them informing later summaries — so "did they have visitors yesterday"
    /// does not accumulate as a history of the same day, over and over. A null expiry (rows
    /// written before this distinction existed) stays visible, matching how those answers already
    /// read back into prompts.
    /// </summary>
    private static bool StillOnTheList(MemberQuestionnaire questionnaire, DateTime utcNow) =>
        questionnaire.Scope == QuestionnaireScope.Permanent
        || questionnaire.ExpiresAtUtc is null
        || questionnaire.ExpiresAtUtc > utcNow;

    private QuestionnaireResponse ToResponse(MemberQuestionnaire questionnaire) => new()
    {
        Id = questionnaire.Id,
        CardiMemberId = questionnaire.CardiMemberId,
        QuestionText = Reveal(questionnaire.QuestionText) ?? string.Empty,
        AnswerText = Reveal(questionnaire.AnswerText),
        TriggerContext = questionnaire.TriggerContext,
        Status = questionnaire.Status.ToString().ToLowerInvariant(),
        GeneratedAtUtc = questionnaire.GeneratedAtUtc,
        AnsweredAtUtc = questionnaire.AnsweredAtUtc,
        AnsweredByUserId = questionnaire.AnsweredByUserId,
        Scope = questionnaire.Scope.ToString().ToLowerInvariant(),
        AskableUntilUtc = questionnaire.AskableUntilUtc,
    };

    /// <summary>
    /// Decrypts, falling back to the stored value. Mirrors <c>CardiMemberService.Reveal</c>: a row
    /// written before encryption is indistinguishable from a corrupt one to AES-GCM, and failing the
    /// whole screen over it would be worse than showing what was typed.
    /// </summary>
    private string? Reveal(string? stored)
    {
        if (string.IsNullOrEmpty(stored))
            return null;

        try
        {
            return _encryption.Decrypt(stored);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or CryptographicException)
        {
            return stored;
        }
    }
}
