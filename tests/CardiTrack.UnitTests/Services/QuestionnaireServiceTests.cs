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
        QuestionnaireStatus status = QuestionnaireStatus.Pending, string? answer = null,
        QuestionnaireScope scope = QuestionnaireScope.TimeScoped, DateTime? expiresAtUtc = null,
        DateTime? askableUntilUtc = null) => new()
        {
            Id = _questionnaireId,
            CardiMemberId = _memberId,
            QuestionText = PromptContextFactory.Encryption.Encrypt("Has anything changed at home recently?"),
            AnswerText = answer is null ? null : PromptContextFactory.Encryption.Encrypt(answer),
            TriggerContext = "Sleep has been shorter all week.",
            Status = status,
            GeneratedAtUtc = new DateTime(2026, 8, 12, 9, 0, 0, DateTimeKind.Utc),
            Scope = scope,
            ExpiresAtUtc = expiresAtUtc,
            AskableUntilUtc = askableUntilUtc,
        };

    [Fact]
    public async Task Listing_DecryptsTheQuestionAndTheAnswer()
    {
        _questionnaires.GetByCardiMemberAsync(_memberId, Arg.Any<CancellationToken>())
            .Returns([Questionnaire(QuestionnaireStatus.Answered, "She moved bedrooms last week.")]);

        var result = await CreateSut().GetForMemberAsync(_userId, _memberId, search: null, page: 1, pageSize: 20);

        var only = Assert.Single(result.Answered.Items);
        Assert.Equal("Has anything changed at home recently?", only.QuestionText);
        Assert.Equal("She moved bedrooms last week.", only.AnswerText);
        Assert.Equal("answered", only.Status);
        Assert.Equal("Sleep has been shorter all week.", only.TriggerContext);
    }

    [Fact]
    public async Task Listing_CarriesTheScope_AsItsLowercaseWireValue()
    {
        _questionnaires.GetByCardiMemberAsync(_memberId, Arg.Any<CancellationToken>())
            .Returns([Questionnaire(QuestionnaireStatus.Answered, "Yes, fitted in 2020.", QuestionnaireScope.Permanent)]);

        var result = await CreateSut().GetForMemberAsync(_userId, _memberId, search: null, page: 1, pageSize: 20);

        Assert.Equal("permanent", Assert.Single(result.Answered.Items).Scope);
    }

    /// <summary>
    /// The Q&amp;A archive is standing facts plus momentary answers that still apply. An expired
    /// "yesterday" must not sit next to a pacemaker answer as if both were still true.
    /// </summary>
    [Fact]
    public async Task Listing_OmitsAnExpiredMomentAnswer_AndKeepsStandingAndCurrentOnes()
    {
        var expired = Questionnaire(
            QuestionnaireStatus.Answered, "No visitors.", QuestionnaireScope.TimeScoped,
            expiresAtUtc: DateTime.UtcNow.AddDays(-1));
        expired.Id = Guid.NewGuid();
        expired.AnsweredAtUtc = new DateTime(2026, 7, 1, 9, 0, 0, DateTimeKind.Utc);

        var currentMoment = Questionnaire(
            QuestionnaireStatus.Answered, "Yes, her sister.", QuestionnaireScope.TimeScoped,
            expiresAtUtc: DateTime.UtcNow.AddDays(7));
        currentMoment.Id = Guid.NewGuid();
        currentMoment.AnsweredAtUtc = new DateTime(2026, 8, 10, 9, 0, 0, DateTimeKind.Utc);

        var standing = Questionnaire(
            QuestionnaireStatus.Answered, "Pacemaker since 2020.", QuestionnaireScope.Permanent);
        standing.Id = Guid.NewGuid();
        standing.AnsweredAtUtc = new DateTime(2026, 8, 1, 9, 0, 0, DateTimeKind.Utc);

        _questionnaires.GetByCardiMemberAsync(_memberId, Arg.Any<CancellationToken>())
            .Returns([expired, currentMoment, standing]);

        var result = await CreateSut().GetForMemberAsync(_userId, _memberId, search: null, page: 1, pageSize: 20);

        Assert.Equal(2, result.Answered.TotalCount);
        Assert.Equal(
            ["Yes, her sister.", "Pacemaker since 2020."],
            result.Answered.Items.Select(q => q.AnswerText));
    }

    /// <summary>A row written before expiry existed is not expired — it already informed prompts
    /// indefinitely, and hiding it from the archive would be a silent deletion.</summary>
    [Fact]
    public async Task Listing_KeepsAMomentAnswerWithNoExpiry()
    {
        _questionnaires.GetByCardiMemberAsync(_memberId, Arg.Any<CancellationToken>())
            .Returns([Questionnaire(QuestionnaireStatus.Answered, "She moved bedrooms.")]);

        var result = await CreateSut().GetForMemberAsync(_userId, _memberId, search: null, page: 1, pageSize: 20);

        Assert.Equal("She moved bedrooms.", Assert.Single(result.Answered.Items).AnswerText);
    }

    /// <summary>
    /// Not-found rather than forbidden, matching the access service: a caller who is refused learns
    /// nothing about whether the member exists.
    /// </summary>
    [Fact]
    public async Task Listing_IsRefusedToSomeoneWhoIsNotTheirCaregiver()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => CreateSut().GetForMemberAsync(_outsiderId, _memberId, search: null, page: 1, pageSize: 20));
    }

    [Fact]
    public async Task Listing_SeparatesThePendingQuestionFromTheAnsweredPage()
    {
        _questionnaires.GetByCardiMemberAsync(_memberId, Arg.Any<CancellationToken>())
            .Returns(
            [
                Questionnaire(QuestionnaireStatus.Pending),
                Questionnaire(QuestionnaireStatus.Answered, "She moved bedrooms."),
                Questionnaire(QuestionnaireStatus.Dismissed),
            ]);

        var result = await CreateSut().GetForMemberAsync(_userId, _memberId, search: null, page: 1, pageSize: 20);

        Assert.NotNull(result.Pending);
        Assert.Equal("pending", result.Pending!.Status);
        var only = Assert.Single(result.Answered.Items);
        Assert.Equal("answered", only.Status);
        Assert.True(result.HasAny);
    }

    /// <summary>
    /// Question and answer text are encrypted at rest, so search has to run after decryption — this
    /// proves it does, against both the question and the answer, case-insensitively.
    /// </summary>
    [Theory]
    [InlineData("bedrooms", true)]
    [InlineData("BEDROOMS", true)]
    [InlineData("changed at home", true)]
    [InlineData("nothing about that", false)]
    public async Task Listing_SearchesTheDecryptedQuestionAndAnswer(string search, bool shouldMatch)
    {
        _questionnaires.GetByCardiMemberAsync(_memberId, Arg.Any<CancellationToken>())
            .Returns([Questionnaire(QuestionnaireStatus.Answered, "She moved bedrooms.")]);

        var result = await CreateSut().GetForMemberAsync(_userId, _memberId, search, page: 1, pageSize: 20);

        Assert.Equal(shouldMatch ? 1 : 0, result.Answered.Items.Count);
        Assert.Equal(shouldMatch ? 1 : 0, result.Answered.TotalCount);
    }

    [Fact]
    public async Task Listing_PagesTheAnsweredHistory_NewestAnswerFirst()
    {
        var older = Questionnaire(QuestionnaireStatus.Answered, "Older answer");
        older.Id = Guid.NewGuid();
        older.AnsweredAtUtc = new DateTime(2026, 8, 1, 9, 0, 0, DateTimeKind.Utc);

        var newer = Questionnaire(QuestionnaireStatus.Answered, "Newer answer");
        newer.Id = Guid.NewGuid();
        newer.AnsweredAtUtc = new DateTime(2026, 8, 10, 9, 0, 0, DateTimeKind.Utc);

        _questionnaires.GetByCardiMemberAsync(_memberId, Arg.Any<CancellationToken>())
            .Returns([older, newer]);

        var firstPage = await CreateSut().GetForMemberAsync(_userId, _memberId, search: null, page: 1, pageSize: 1);
        Assert.Equal("Newer answer", firstPage.Answered.Items.Single().AnswerText);
        Assert.True(firstPage.Answered.HasMore);
        Assert.Equal(2, firstPage.Answered.TotalCount);

        var secondPage = await CreateSut().GetForMemberAsync(_userId, _memberId, search: null, page: 2, pageSize: 1);
        Assert.Equal("Older answer", secondPage.Answered.Items.Single().AnswerText);
        Assert.False(secondPage.Answered.HasMore);
    }

    [Fact]
    public async Task Listing_ReportsNoHistory_WhenTheMemberHasNeverBeenAsked()
    {
        _questionnaires.GetByCardiMemberAsync(_memberId, Arg.Any<CancellationToken>()).Returns([]);

        var result = await CreateSut().GetForMemberAsync(_userId, _memberId, search: null, page: 1, pageSize: 20);

        Assert.False(result.HasAny);
        Assert.Null(result.Pending);
        Assert.Empty(result.Answered.Items);
    }

    /// <summary>
    /// Only pageSize is clamped before this method sees it (see QuestionnairesController) — an
    /// extreme page number must still resolve to an empty page rather than overflow the int
    /// arithmetic behind Skip/HasMore.
    /// </summary>
    [Fact]
    public async Task Listing_ReturnsAnEmptyPage_ForAnExtremePageNumber_WithoutOverflowing()
    {
        _questionnaires.GetByCardiMemberAsync(_memberId, Arg.Any<CancellationToken>())
            .Returns([Questionnaire(QuestionnaireStatus.Answered, "She moved bedrooms.")]);

        var result = await CreateSut().GetForMemberAsync(
            _userId, _memberId, search: null, page: int.MaxValue, pageSize: 50);

        Assert.Empty(result.Answered.Items);
        Assert.False(result.Answered.HasMore);
        Assert.Equal(1, result.Answered.TotalCount);
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

    // ---- Questions that outlived the day they asked about ----

    /// <summary>
    /// The failure, in one test: a question generated one evening, still Pending the next morning,
    /// and served to a caregiver as "did he feel tired at all today?" about a day already over.
    /// </summary>
    [Fact]
    public async Task Listing_NeverServesAPendingQuestionPastItsDay()
    {
        _questionnaires.GetByCardiMemberAsync(_memberId, Arg.Any<CancellationToken>())
            .Returns([Questionnaire(askableUntilUtc: DateTime.UtcNow.AddMinutes(-1))]);

        var result = await CreateSut().GetForMemberAsync(_userId, _memberId, search: null, page: 1, pageSize: 20);

        Assert.Null(result.Pending);
        // Still on file, and the page still knows there is history — only the card is gone.
        Assert.True(result.HasAny);
    }

    [Fact]
    public async Task Listing_StillServesAPendingQuestionInsideItsDay()
    {
        _questionnaires.GetByCardiMemberAsync(_memberId, Arg.Any<CancellationToken>())
            .Returns([Questionnaire(askableUntilUtc: DateTime.UtcNow.AddHours(2))]);

        var result = await CreateSut().GetForMemberAsync(_userId, _memberId, search: null, page: 1, pageSize: 20);

        Assert.NotNull(result.Pending);
        Assert.Equal("Has anything changed at home recently?", result.Pending.QuestionText);
    }

    /// <summary>
    /// Rows written before questions carried a validity, and standing-fact questions, both stay
    /// askable — a null deadline is "never lapses", not "lapsed at the epoch".
    /// </summary>
    [Fact]
    public async Task Listing_TreatsAQuestionWithNoDeadline_AsStillWorthAsking()
    {
        _questionnaires.GetByCardiMemberAsync(_memberId, Arg.Any<CancellationToken>())
            .Returns([Questionnaire(askableUntilUtc: null)]);

        var result = await CreateSut().GetForMemberAsync(_userId, _memberId, search: null, page: 1, pageSize: 20);

        Assert.NotNull(result.Pending);
    }

    [Fact]
    public async Task Expiring_RetiresAQuestionWhoseDayHasEnded()
    {
        var stored = Questionnaire(askableUntilUtc: DateTime.UtcNow.AddMinutes(-1));
        _questionnaires.GetByIdAsync(_questionnaireId).Returns(stored);

        var result = await CreateSut().ExpireAsync(_userId, _questionnaireId);

        Assert.Equal(QuestionnaireStatus.Expired, stored.Status);
        Assert.Equal("expired", result.Status);
        await _unitOfWork.Received(1).SaveChangesAsync();
    }

    /// <summary>
    /// The server's clock decides, not the caller's claim. A device running fast would otherwise
    /// retire a question the rest of the family still has in front of them.
    /// </summary>
    [Fact]
    public async Task Expiring_LeavesAQuestionStillInsideItsDayAlone()
    {
        var stored = Questionnaire(askableUntilUtc: DateTime.UtcNow.AddHours(2));
        _questionnaires.GetByIdAsync(_questionnaireId).Returns(stored);

        var result = await CreateSut().ExpireAsync(_userId, _questionnaireId);

        Assert.Equal(QuestionnaireStatus.Pending, stored.Status);
        Assert.Equal("pending", result.Status);
        await _unitOfWork.DidNotReceive().SaveChangesAsync();
    }

    /// <summary>
    /// Two caregivers can open the same member at once, and the sweep runs alongside both. A second
    /// call answers with the row as it stands rather than failing.
    /// </summary>
    [Fact]
    public async Task Expiring_IsIdempotent()
    {
        var stored = Questionnaire(
            QuestionnaireStatus.Expired, askableUntilUtc: DateTime.UtcNow.AddMinutes(-1));
        _questionnaires.GetByIdAsync(_questionnaireId).Returns(stored);

        var result = await CreateSut().ExpireAsync(_userId, _questionnaireId);

        Assert.Equal("expired", result.Status);
        await _unitOfWork.DidNotReceive().SaveChangesAsync();
    }

    /// <summary>
    /// Unlike dismissing, which is the family's decision and a promise never to ask that again.
    /// Nobody decided this, so an answered or dismissed row is never quietly overwritten by it.
    /// </summary>
    [Theory]
    [InlineData(QuestionnaireStatus.Answered)]
    [InlineData(QuestionnaireStatus.Dismissed)]
    public async Task Expiring_NeverOverwritesAQuestionSomeoneSettled(QuestionnaireStatus status)
    {
        var stored = Questionnaire(status, askableUntilUtc: DateTime.UtcNow.AddDays(-3));
        _questionnaires.GetByIdAsync(_questionnaireId).Returns(stored);

        await CreateSut().ExpireAsync(_userId, _questionnaireId);

        Assert.Equal(status, stored.Status);
        await _unitOfWork.DidNotReceive().SaveChangesAsync();
    }

    [Fact]
    public async Task Listing_CarriesTheAskDeadline_SoTheAppsCanTellWithoutARoundTrip()
    {
        var until = DateTime.UtcNow.AddHours(2);
        _questionnaires.GetByCardiMemberAsync(_memberId, Arg.Any<CancellationToken>())
            .Returns([Questionnaire(askableUntilUtc: until)]);

        var result = await CreateSut().GetForMemberAsync(_userId, _memberId, search: null, page: 1, pageSize: 20);

        Assert.Equal(until, result.Pending!.AskableUntilUtc);
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
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => CreateSut().ExpireAsync(requestingUserId, _questionnaireId));
    }
}
