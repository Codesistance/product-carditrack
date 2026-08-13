using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using NSubstitute;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// What a caregiver may do with a question the service asked, and what someone who is not their
/// caregiver may not.
/// </summary>
public class QuestionnaireServiceTests
{
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IMemberQuestionnaireRepository _questionnaires =
        Substitute.For<IMemberQuestionnaireRepository>();
    private readonly IUserCardiMemberRepository _links = Substitute.For<IUserCardiMemberRepository>();

    private readonly Guid _memberId = Guid.NewGuid();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _outsiderId = Guid.NewGuid();
    private readonly Guid _questionnaireId = Guid.NewGuid();

    public QuestionnaireServiceTests()
    {
        _unitOfWork.MemberQuestionnaires.Returns(_questionnaires);
        _unitOfWork.UserCardiMembers.Returns(_links);

        _links.GetByUserIdAsync(_userId).Returns(
        [
            new UserCardiMember { UserId = _userId, CardiMemberId = _memberId, IsActive = true },
        ]);
        _links.GetByUserIdAsync(_outsiderId).Returns([]);

        _questionnaires.GetByIdAsync(_questionnaireId).Returns(Questionnaire());
    }

    private QuestionnaireService CreateSut() =>
        new(_unitOfWork, new CardiMemberAccessService(_unitOfWork), PromptContextFactory.Encryption);

    private MemberQuestionnaire Questionnaire(
        QuestionnaireStatus status = QuestionnaireStatus.Pending, string? answer = null) => new()
    {
        Id = _questionnaireId,
        CardiMemberId = _memberId,
        QuestionText = PromptContextFactory.Encryption.Encrypt("Has anything changed at home recently?"),
        AnswerText = answer is null ? null : PromptContextFactory.Encryption.Encrypt(answer),
        TriggerContext = "Sleep has been shorter all week.",
        Status = status,
        GeneratedAtUtc = new DateTime(2026, 8, 12, 9, 0, 0, DateTimeKind.Utc),
    };

    [Fact]
    public async Task Listing_DecryptsTheQuestionAndTheAnswer()
    {
        _questionnaires.GetByCardiMemberAsync(_memberId, Arg.Any<CancellationToken>())
            .Returns([Questionnaire(QuestionnaireStatus.Answered, "She moved bedrooms last week.")]);

        var result = await CreateSut().GetForMemberAsync(_userId, _memberId);

        var only = Assert.Single(result);
        Assert.Equal("Has anything changed at home recently?", only.QuestionText);
        Assert.Equal("She moved bedrooms last week.", only.AnswerText);
        Assert.Equal("answered", only.Status);
        Assert.Equal("Sleep has been shorter all week.", only.TriggerContext);
    }

    /// <summary>
    /// Not-found rather than forbidden, matching the access service: a caller who is refused learns
    /// nothing about whether the member exists.
    /// </summary>
    [Fact]
    public async Task Listing_IsRefusedToSomeoneWhoIsNotTheirCaregiver()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => CreateSut().GetForMemberAsync(_outsiderId, _memberId));
    }

    [Fact]
    public async Task Answering_StoresTheAnswerEncrypted_AndStampsWhoAndWhen()
    {
        var stored = Questionnaire();
        _questionnaires.GetByIdAsync(_questionnaireId).Returns(stored);

        var result = await CreateSut().AnswerAsync(_userId, _questionnaireId, "  She moved bedrooms.  ");

        Assert.Equal(QuestionnaireStatus.Answered, stored.Status);
        Assert.NotNull(stored.AnswerText);
        Assert.NotEqual("She moved bedrooms.", stored.AnswerText);
        Assert.Equal("She moved bedrooms.", PromptContextFactory.Encryption.Decrypt(stored.AnswerText!));
        Assert.Equal(_userId, stored.AnsweredByUserId);
        Assert.NotNull(stored.AnsweredAtUtc);
        Assert.Equal("She moved bedrooms.", result.AnswerText);
        await _unitOfWork.Received(1).SaveChangesAsync();
    }

    /// <summary>
    /// Editing is the same act as answering. What a caregiver wants beside an answer is when it was
    /// last true, so the timestamp moves with the edit.
    /// </summary>
    [Fact]
    public async Task Answering_Again_ReplacesTheAnswer()
    {
        var stored = Questionnaire(QuestionnaireStatus.Answered, "She moved bedrooms.");
        var answeredAt = new DateTime(2026, 8, 12, 10, 0, 0, DateTimeKind.Utc);
        stored.AnsweredAtUtc = answeredAt;
        _questionnaires.GetByIdAsync(_questionnaireId).Returns(stored);

        var result = await CreateSut().AnswerAsync(_userId, _questionnaireId, "She moved back again.");

        Assert.Equal("She moved back again.", result.AnswerText);
        Assert.True(stored.AnsweredAtUtc > answeredAt);
    }

    [Fact]
    public async Task Dismissing_KeepsTheRecordOfHavingAsked()
    {
        var stored = Questionnaire();
        _questionnaires.GetByIdAsync(_questionnaireId).Returns(stored);

        var result = await CreateSut().DismissAsync(_userId, _questionnaireId);

        Assert.Equal(QuestionnaireStatus.Dismissed, stored.Status);
        Assert.Equal("dismissed", result.Status);
        Assert.Null(stored.AnswerText);
        _questionnaires.DidNotReceive().Remove(Arg.Any<MemberQuestionnaire>());
    }

    /// <summary>
    /// A real row delete, not a flag: the answer is something a family member wrote about a person
    /// who never signed up to this service, so erasure has to mean gone (GDPR Art. 17).
    /// </summary>
    [Fact]
    public async Task Deleting_RemovesTheRow()
    {
        var stored = Questionnaire(QuestionnaireStatus.Answered, "She moved bedrooms.");
        _questionnaires.GetByIdAsync(_questionnaireId).Returns(stored);

        await CreateSut().DeleteAsync(_userId, _questionnaireId);

        _questionnaires.Received(1).Remove(stored);
        await _unitOfWork.Received(1).SaveChangesAsync();
    }

    /// <summary>
    /// "No such questionnaire" and "not your questionnaire" report the same not-found, so the id
    /// space cannot be probed for which questions exist about whom.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnUnreachableQuestionnaire_IsAlwaysNotFound(bool exists)
    {
        if (!exists)
            _questionnaires.GetByIdAsync(_questionnaireId).Returns((MemberQuestionnaire?)null);

        var requestingUserId = exists ? _outsiderId : _userId;

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => CreateSut().AnswerAsync(requestingUserId, _questionnaireId, "anything"));
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => CreateSut().DeleteAsync(requestingUserId, _questionnaireId));
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => CreateSut().DismissAsync(requestingUserId, _questionnaireId));
    }
}
