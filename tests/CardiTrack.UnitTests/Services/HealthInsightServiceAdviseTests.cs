using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Infrastructure.Services;
using NSubstitute;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// <see cref="HealthInsightService.GetAdviseAsync"/> — the CardiMember Details Tip card's wellness
/// suggestion, read-only: the pipeline generates and persists it
/// (<see cref="AdviseGenerationServiceTests"/> pins that side), and this endpoint serves the latest
/// row. What these tests pin is the serving contract — access, the fresh row, and every case that
/// must answer "nothing to suggest" instead of something stale or false — the same shape
/// <see cref="HealthInsightServiceStatusTests"/> pins for the status line.
/// </summary>
public class HealthInsightServiceAdviseTests
{
    private readonly IMedicalAiService _medicalAi = Substitute.For<IMedicalAiService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IUserCardiMemberRepository _links = Substitute.For<IUserCardiMemberRepository>();
    private readonly ICardiMemberRepository _members = Substitute.For<ICardiMemberRepository>();
    private readonly IMemberAdviseRepository _advises = Substitute.For<IMemberAdviseRepository>();

    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _outsiderId = Guid.NewGuid();
    private readonly Guid _memberId = Guid.NewGuid();

    public HealthInsightServiceAdviseTests()
    {
        _unitOfWork.UserCardiMembers.Returns(_links);
        _unitOfWork.CardiMembers.Returns(_members);
        _unitOfWork.MemberAdvises.Returns(_advises);

        _links.GetByUserIdAsync(_userId).Returns([
            new UserCardiMember
            {
                UserId = _userId,
                CardiMemberId = _memberId,
                IsActive = true,
                CanViewHealthData = true,
            },
        ]);
        _links.GetByUserIdAsync(_outsiderId).Returns([
            new UserCardiMember
            {
                UserId = _outsiderId,
                CardiMemberId = Guid.NewGuid(),
                IsActive = true,
                CanViewHealthData = true,
            },
        ]);

        _members.GetByIdAsync(_memberId).Returns(new CardiMember
        {
            Id = _memberId,
            Name = "Margaret Doe",
            DateOfBirth = new DateOnly(1948, 3, 15),
            IsActive = true,
        });
        _advises.GetByCardiMemberAsync(_memberId).Returns(new MemberAdvise
        {
            CardiMemberId = _memberId,
            Summary = "Steps have been below her usual this week.",
            Suggestion = "A short walk after lunch is worth trying.",
            GuidelineCited = "WHO adult activity guidance",
            GeneratedAtUtc = DateTime.UtcNow.AddHours(-2),
        });
    }

    private HealthInsightService CreateSut() =>
        new(_medicalAi, _unitOfWork, new CardiMemberAccessService(_unitOfWork),
            PromptContextFactory.Composer(_unitOfWork));

    [Fact]
    public async Task ServesTheStoredRow_ForALinkedUser()
    {
        var result = await CreateSut().GetAdviseAsync(_userId, _memberId);

        Assert.Equal("Steps have been below her usual this week.", result.Summary);
        Assert.Equal("A short walk after lunch is worth trying.", result.Suggestion);
        Assert.Equal("WHO adult activity guidance", result.GuidelineCited);
    }

    [Fact]
    public async Task ReportsTheRowsGenerationTime_NotTheReadTime()
    {
        var generatedAt = DateTime.UtcNow.AddHours(-2);
        _advises.GetByCardiMemberAsync(_memberId).Returns(new MemberAdvise
        {
            CardiMemberId = _memberId,
            Summary = "Summary.",
            Suggestion = "Suggestion.",
            GeneratedAtUtc = generatedAt,
        });

        var result = await CreateSut().GetAdviseAsync(_userId, _memberId);

        Assert.Equal(new DateTimeOffset(DateTime.SpecifyKind(generatedAt, DateTimeKind.Utc)), result.GeneratedAt);
    }

    [Fact]
    public async Task Throws_ForAUserNotLinkedToTheMember()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            CreateSut().GetAdviseAsync(_outsiderId, _memberId));
    }

    /// <summary>This endpoint never spends a model call — the whole point of the batch move.</summary>
    [Fact]
    public async Task NeverCallsTheModel()
    {
        await CreateSut().GetAdviseAsync(_userId, _memberId);

        Assert.Empty(_medicalAi.ReceivedCalls());
    }

    [Fact]
    public async Task NoRowYet_AnswersNothingToSuggest()
    {
        _advises.GetByCardiMemberAsync(_memberId).Returns((MemberAdvise?)null);

        var result = await CreateSut().GetAdviseAsync(_userId, _memberId);

        Assert.Equal(string.Empty, result.Summary);
        Assert.Equal(string.Empty, result.Suggestion);
        Assert.Null(result.GuidelineCited);
    }

    /// <summary>
    /// A stale row means generation has stopped for this member, and yesterday's suggestion
    /// presented as current would say something false.
    /// </summary>
    [Fact]
    public async Task StaleRow_IsWithheld()
    {
        _advises.GetByCardiMemberAsync(_memberId).Returns(new MemberAdvise
        {
            CardiMemberId = _memberId,
            Summary = "Old summary.",
            Suggestion = "Old suggestion.",
            GeneratedAtUtc = DateTime.UtcNow.AddDays(-4),
        });

        var result = await CreateSut().GetAdviseAsync(_userId, _memberId);

        Assert.Equal(string.Empty, result.Suggestion);
    }

    [Fact]
    public async Task RowJustInsideTheCeiling_StillServes()
    {
        _advises.GetByCardiMemberAsync(_memberId).Returns(new MemberAdvise
        {
            CardiMemberId = _memberId,
            Summary = "Recent summary.",
            Suggestion = "Recent suggestion.",
            GeneratedAtUtc = DateTime.UtcNow.AddDays(-2),
        });

        var result = await CreateSut().GetAdviseAsync(_userId, _memberId);

        Assert.Equal("Recent suggestion.", result.Suggestion);
    }

    /// <summary>A paused member's stored suggestion describes a monitoring state that no longer
    /// exists — serving it would say something false.</summary>
    [Fact]
    public async Task PausedMember_AnswersNothingToSuggest()
    {
        _members.GetByIdAsync(_memberId).Returns(new CardiMember
        {
            Id = _memberId,
            Name = "Margaret Doe",
            IsActive = true,
            MonitoringPausedUntil = DateTime.UtcNow.AddHours(4),
        });

        var result = await CreateSut().GetAdviseAsync(_userId, _memberId);

        Assert.Equal(string.Empty, result.Suggestion);
    }

    [Fact]
    public async Task InactiveMember_AnswersNothingToSuggest()
    {
        _members.GetByIdAsync(_memberId).Returns(new CardiMember
        {
            Id = _memberId,
            Name = "Margaret Doe",
            IsActive = false,
        });

        var result = await CreateSut().GetAdviseAsync(_userId, _memberId);

        Assert.Equal(string.Empty, result.Suggestion);
    }
}
