using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Services;
using NSubstitute;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// <see cref="HealthInsightService.GetAdviseObservationsAsync"/> — the dated record behind the
/// single current suggestion <see cref="HealthInsightServiceAdviseTests"/> pins. What matters here
/// is the window: what it clamps, what it reports back, and that it never quietly serves a
/// different span from the one it names.
/// </summary>
public class HealthInsightServiceObservationTests
{
    private readonly IMedicalAiService _medicalAi = Substitute.For<IMedicalAiService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IUserCardiMemberRepository _links = Substitute.For<IUserCardiMemberRepository>();
    private readonly ICardiMemberRepository _members = Substitute.For<ICardiMemberRepository>();
    private readonly IMemberAdviseObservationRepository _observations =
        Substitute.For<IMemberAdviseObservationRepository>();

    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _outsiderId = Guid.NewGuid();
    private readonly Guid _memberId = Guid.NewGuid();

    public HealthInsightServiceObservationTests()
    {
        _unitOfWork.UserCardiMembers.Returns(_links);
        _unitOfWork.CardiMembers.Returns(_members);
        _unitOfWork.MemberAdviseObservations.Returns(_observations);

        _links.GetByUserIdAsync(_userId).Returns([
            new UserCardiMember
            {
                UserId = _userId,
                CardiMemberId = _memberId,
                IsActive = true,
                CanViewHealthData = true,
            },
        ]);
        _links.GetByUserIdAsync(_outsiderId).Returns([]);

        _members.GetByIdAsync(_memberId).Returns(new CardiMember
        {
            Id = _memberId,
            Name = "Margaret Doe",
            DateOfBirth = new DateOnly(1948, 3, 15),
            IsActive = true,
        });

        Holds();
    }

    private void Holds(params MemberAdviseObservation[] rows) =>
        _observations.GetByCardiMemberAsync(
                Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<DateTime>(),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(rows);

    private static MemberAdviseObservation Row(int daysAgo, string summary = "Steps were down.") => new()
    {
        Topic = AdviseTopic.Activity,
        Summary = summary,
        Suggestion = "A short walk after lunch is worth trying.",
        GuidelineCited = "WHO adult activity guidance",
        ObservedAtUtc = DateTime.UtcNow.AddDays(-daysAgo),
    };

    private HealthInsightService CreateSut() =>
        new(_medicalAi, _unitOfWork, new CardiMemberAccessService(_unitOfWork),
            PromptContextFactory.Composer(_unitOfWork));

    [Fact]
    public async Task ACaregiverWhoCannotViewTheMember_IsRefused()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => CreateSut().GetAdviseObservationsAsync(_outsiderId, _memberId));
    }

    [Fact]
    public async Task AMemberWithNothingLogged_ReadsAsAnEmptyRecord()
    {
        var log = await CreateSut().GetAdviseObservationsAsync(_userId, _memberId);

        Assert.Empty(log.Observations);
        Assert.False(log.Truncated);
        Assert.Equal(_memberId, log.CardiMemberId);
    }

    [Fact]
    public async Task EntriesComeBackWithTheirTopicDateAndGrounding()
    {
        Holds(Row(3));

        var log = await CreateSut().GetAdviseObservationsAsync(_userId, _memberId);

        var entry = Assert.Single(log.Observations);
        Assert.Equal(AdviseTopic.Activity, entry.Topic);
        Assert.Equal("Steps were down.", entry.Summary);
        Assert.Equal("WHO adult activity guidance", entry.GuidelineCited);
        Assert.Equal(TimeSpan.Zero, entry.ObservedAt.Offset);
    }

    /// <summary>
    /// The clamp the whole response contract rests on: a caller asking for five years must be told
    /// it got one, in the window the response itself names, rather than being left to assume the
    /// empty stretch at the far end means nothing happened.
    /// </summary>
    [Fact]
    public async Task AWindowReachingPastRetention_IsClampedAndSaidSo()
    {
        var asked = DateTimeOffset.UtcNow.AddYears(-5);

        var log = await CreateSut().GetAdviseObservationsAsync(_userId, _memberId, from: asked);

        Assert.True(log.From > asked);
        var horizon = DateTimeOffset.UtcNow - AdviseObservationRetention.Period;
        Assert.True((log.From - horizon).Duration() < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task AWindowReachingIntoTheFuture_EndsAtNow()
    {
        var asked = DateTimeOffset.UtcNow.AddDays(30);

        var log = await CreateSut().GetAdviseObservationsAsync(_userId, _memberId, to: asked);

        Assert.True(log.To < asked);
    }

    /// <summary>
    /// A window containing no time is answered rather than refused: the caller asked what happened
    /// between two instants, and for an inverted pair "nothing" is the true answer.
    /// </summary>
    [Fact]
    public async Task AnInvertedWindow_ReadsAsEmptyRatherThanAnError()
    {
        Holds(Row(3));

        var log = await CreateSut().GetAdviseObservationsAsync(
            _userId, _memberId,
            from: DateTimeOffset.UtcNow.AddDays(-1),
            to: DateTimeOffset.UtcNow.AddDays(-10));

        Assert.Empty(log.Observations);
        await _observations.DidNotReceive().GetByCardiMemberAsync(
            Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<DateTime>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AWindowHoldingMoreThanTheLimit_IsTrimmedAndSaysSo()
    {
        Holds([.. Enumerable.Range(0, HealthInsightService.ObservationLogLimit + 1).Select(i => Row(i))]);

        var log = await CreateSut().GetAdviseObservationsAsync(_userId, _memberId);

        Assert.Equal(HealthInsightService.ObservationLogLimit, log.Observations.Count);
        Assert.True(log.Truncated);
    }

    [Fact]
    public async Task AWindowHoldingExactlyTheLimit_IsNotReportedAsTrimmed()
    {
        Holds([.. Enumerable.Range(0, HealthInsightService.ObservationLogLimit).Select(i => Row(i))]);

        var log = await CreateSut().GetAdviseObservationsAsync(_userId, _memberId);

        Assert.Equal(HealthInsightService.ObservationLogLimit, log.Observations.Count);
        Assert.False(log.Truncated);
    }

    /// <summary>
    /// Unlike the current suggestion, a pause does not withhold the record. What was noticed in
    /// March was noticed in March, and a pause today does not make it untrue — see
    /// <see cref="HealthInsightService.GetAdviseObservationsAsync"/>.
    /// </summary>
    [Fact]
    public async Task APausedMembersRecordIsStillServed()
    {
        _members.GetByIdAsync(_memberId).Returns(new CardiMember
        {
            Id = _memberId,
            Name = "Margaret Doe",
            DateOfBirth = new DateOnly(1948, 3, 15),
            IsActive = true,
            MonitoringPausedUntil = DateTime.UtcNow.AddDays(7),
        });
        Holds(Row(3));

        var log = await CreateSut().GetAdviseObservationsAsync(_userId, _memberId);

        Assert.Single(log.Observations);
    }
}
