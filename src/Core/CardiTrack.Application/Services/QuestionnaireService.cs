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

    public async Task<IReadOnlyList<QuestionnaireResponse>> GetForMemberAsync(
        Guid requestingUserId, Guid cardiMemberId, CancellationToken ct = default)
    {
        await _access.RequireViewAccessAsync(requestingUserId, cardiMemberId, ct);

        var questionnaires = await _unitOfWork.MemberQuestionnaires.GetByCardiMemberAsync(cardiMemberId, ct);
        return questionnaires.Select(ToResponse).ToList();
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
