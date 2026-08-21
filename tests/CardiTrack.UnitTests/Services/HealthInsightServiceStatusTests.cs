using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Infrastructure.Services;
using NSubstitute;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// <see cref="HealthInsightService.GetCurrentStatusMessageAsync"/> — the Dashboard hero card's
/// status line, read-only since the batch move: the pipeline generates and persists the line
/// (<see cref="StatusLineGenerationServiceTests"/> pins that side), and this endpoint serves the
/// latest row. What these tests pin is the serving contract: access, the fresh row, and every
/// case that must answer "nothing to say yet" instead of something stale or false.
/// </summary>
public class HealthInsightServiceStatusTests
{
    private readonly IMedicalAiService _medicalAi = Substitute.For<IMedicalAiService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IUserCardiMemberRepository _links = Substitute.For<IUserCardiMemberRepository>();
    private readonly ICardiMemberRepository _members = Substitute.For<ICardiMemberRepository>();
    private readonly IMemberStatusLineRepository _statusLines = Substitute.For<IMemberStatusLineRepository>();

    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _outsiderId = Guid.NewGuid();
    private readonly Guid _memberId = Guid.NewGuid();

    public HealthInsightServiceStatusTests()
    {
        _unitOfWork.UserCardiMembers.Returns(_links);
        _unitOfWork.CardiMembers.Returns(_members);
        _unitOfWork.MemberStatusLines.Returns(_statusLines);

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
        _statusLines.GetByCardiMemberAsync(_memberId).Returns(new MemberStatusLine
        {
            CardiMemberId = _memberId,
            Headline = "All steady",
            Message = "Margaret seems steady today.",
            GeneratedAtUtc = DateTime.UtcNow.AddMinutes(-10),
        });
    }

    private HealthInsightService CreateSut() =>
        new(_medicalAi, _unitOfWork, new CardiMemberAccessService(_unitOfWork),
            PromptContextFactory.Composer(_unitOfWork));

    [Fact]
    public async Task ServesTheStoredRow_ForALinkedUser()
    {
        var result = await CreateSut().GetCurrentStatusMessageAsync(_userId, _memberId);

        Assert.Equal("All steady", result.Headline);
        Assert.Equal("Margaret seems steady today.", result.Message);
    }

    /// <summary>The row's generation time, not the moment this call happened to read it — the
    /// client captions "as of …" from this.</summary>
    [Fact]
    public async Task ReportsTheRowsGenerationTime_NotTheReadTime()
    {
        var generatedAt = DateTime.UtcNow.AddMinutes(-10);
        _statusLines.GetByCardiMemberAsync(_memberId).Returns(new MemberStatusLine
        {
            CardiMemberId = _memberId,
            Message = "Margaret seems steady today.",
            GeneratedAtUtc = generatedAt,
        });

        var result = await CreateSut().GetCurrentStatusMessageAsync(_userId, _memberId);

        Assert.Equal(new DateTimeOffset(DateTime.SpecifyKind(generatedAt, DateTimeKind.Utc)), result.GeneratedAt);
    }

    [Fact]
    public async Task Throws_ForAUserNotLinkedToTheMember()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            CreateSut().GetCurrentStatusMessageAsync(_outsiderId, _memberId));
    }

    /// <summary>This endpoint never spends a model call — that is the whole point of the batch
    /// move, and the reason MedGemma can scale to zero between batch windows.</summary>
    [Fact]
    public async Task NeverCallsTheModel()
    {
        await CreateSut().GetCurrentStatusMessageAsync(_userId, _memberId);

        Assert.Empty(_medicalAi.ReceivedCalls());
    }

    [Fact]
    public async Task NoRowYet_AnswersNothingToSay()
    {
        _statusLines.GetByCardiMemberAsync(_memberId).Returns((MemberStatusLine?)null);

        var result = await CreateSut().GetCurrentStatusMessageAsync(_userId, _memberId);

        Assert.Null(result.Message);
        Assert.Null(result.Headline);
    }

    /// <summary>
    /// A day-old row means generation has stopped for this member, and yesterday's reassurance
    /// presented as current would say something false — the per-tier fallback is the honest
    /// answer then.
    /// </summary>
    [Fact]
    public async Task StaleRow_IsWithheld()
    {
        _statusLines.GetByCardiMemberAsync(_memberId).Returns(new MemberStatusLine
        {
            CardiMemberId = _memberId,
            Message = "A line from yesterday.",
            GeneratedAtUtc = DateTime.UtcNow.AddHours(-25),
        });

        var result = await CreateSut().GetCurrentStatusMessageAsync(_userId, _memberId);

        Assert.Null(result.Message);
    }

    [Fact]
    public async Task RowJustInsideTheCeiling_StillServes()
    {
        _statusLines.GetByCardiMemberAsync(_memberId).Returns(new MemberStatusLine
        {
            CardiMemberId = _memberId,
            Message = "A line from this morning.",
            GeneratedAtUtc = DateTime.UtcNow.AddHours(-23),
        });

        var result = await CreateSut().GetCurrentStatusMessageAsync(_userId, _memberId);

        Assert.Equal("A line from this morning.", result.Message);
    }

    /// <summary>A paused member's stored line describes a monitoring state that no longer
    /// exists — serving it would say something false.</summary>
    [Fact]
    public async Task PausedMember_AnswersNothingToSay()
    {
        _members.GetByIdAsync(_memberId).Returns(new CardiMember
        {
            Id = _memberId,
            Name = "Margaret Doe",
            IsActive = true,
            MonitoringPausedUntil = DateTime.UtcNow.AddHours(4),
        });

        var result = await CreateSut().GetCurrentStatusMessageAsync(_userId, _memberId);

        Assert.Null(result.Message);
    }

    /// <summary>A pause that has elapsed is not a pause — the member is being watched again.</summary>
    [Fact]
    public async Task ExpiredPause_StillServes()
    {
        _members.GetByIdAsync(_memberId).Returns(new CardiMember
        {
            Id = _memberId,
            Name = "Margaret Doe",
            IsActive = true,
            MonitoringPausedUntil = DateTime.UtcNow.AddHours(-1),
        });

        var result = await CreateSut().GetCurrentStatusMessageAsync(_userId, _memberId);

        Assert.Equal("Margaret seems steady today.", result.Message);
    }

    [Fact]
    public async Task InactiveMember_AnswersNothingToSay()
    {
        _members.GetByIdAsync(_memberId).Returns(new CardiMember
        {
            Id = _memberId,
            Name = "Margaret Doe",
            IsActive = false,
        });

        var result = await CreateSut().GetCurrentStatusMessageAsync(_userId, _memberId);

        Assert.Null(result.Message);
    }
}
