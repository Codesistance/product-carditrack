using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// Pins the digest due-ness rules: 06:00 in the member's anchor timezone, once per local day,
/// only for members with yesterday's data, never while paused — and one member's failure never
/// costs another family their digest.
/// </summary>
public class DigestGenerationServiceTests
{
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ICardiMemberRepository _members = Substitute.For<ICardiMemberRepository>();
    private readonly IUserCardiMemberRepository _links = Substitute.For<IUserCardiMemberRepository>();
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IActivityLogRepository _activityLogs = Substitute.For<IActivityLogRepository>();
    private readonly IDigestRepository _digests = Substitute.For<IDigestRepository>();
    private readonly IMedicalAiService _medicalAi = Substitute.For<IMedicalAiService>();

    private readonly Guid _memberId = Guid.NewGuid();
    private readonly Guid _userId = Guid.NewGuid();

    /// <summary>05:30 UTC on a BST date: 06:30 in London (due), 05:30 in UTC (not due).</summary>
    private static readonly DateTime UtcNow = new(2026, 8, 10, 5, 30, 0, DateTimeKind.Utc);

    public DigestGenerationServiceTests()
    {
        _unitOfWork.CardiMembers.Returns(_members);
        _unitOfWork.UserCardiMembers.Returns(_links);
        _unitOfWork.Users.Returns(_users);
        _unitOfWork.ActivityLogs.Returns(_activityLogs);
        _unitOfWork.Digests.Returns(_digests);

        // Defaults: one active London-anchored member with data yesterday, no digest yet.
        _members.GetActiveIdsWithActivitySinceAsync(Arg.Any<DateOnly>()).Returns([_memberId]);
        _members.GetByIdAsync(_memberId).Returns(Member());
        SetupAnchorTimeZone("Europe/London");
        _activityLogs.GetByCardiMemberAndDateRangeAsync(_memberId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns(call =>
            [
                new ActivityLog { CardiMemberId = _memberId, Date = call.ArgAt<DateOnly>(1), Steps = 5000, RestingHeartRate = 68 },
            ]);
        _medicalAi.GenerateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("A settled day: steady heart rate and a good night's sleep.");
    }

    private CardiMember Member() => new()
    {
        Id = _memberId,
        Name = "Margaret Doe",
        DateOfBirth = new DateOnly(1948, 3, 2),
        Gender = Gender.Female,
        IsActive = true,
    };

    private void SetupAnchorTimeZone(string timeZoneId)
    {
        _links.GetByCardiMemberIdAsync(_memberId).Returns(
        [
            new UserCardiMember { UserId = _userId, CardiMemberId = _memberId, IsActive = true },
        ]);
        _users.GetByIdAsync(_userId).Returns(new User { Id = _userId, TimeZoneId = timeZoneId });
    }

    private DigestGenerationService CreateSut() =>
        new(_unitOfWork, _medicalAi, NullLogger<DigestGenerationService>.Instance);

    [Fact]
    public async Task Generates_WhenTheAnchorTimezoneIsInItsSixOClockHour()
    {
        var generated = await CreateSut().GenerateDueDigestsAsync(UtcNow);

        Assert.Equal(1, generated);
        await _digests.Received(1).UpsertAsync(
            Arg.Is<DigestEntry>(d =>
                d.CardiMemberId == _memberId &&
                d.Audience == DigestAudience.Family &&
                // Keyed by the day the text DESCRIBES (yesterday), not the delivery day —
                // the API contract's `localDate` is the day the digest is about.
                d.LocalDate == new DateOnly(2026, 8, 9) &&
                d.Text.Contains("settled") &&
                d.GeneratedAtUtc == UtcNow),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Prompt_CarriesTheFramingAndYesterdaysReadings_NeverTheName()
    {
        await CreateSut().GenerateDueDigestsAsync(UtcNow);

        var prompt = (string)_medicalAi.ReceivedCalls().Single().GetArguments()[0]!;
        Assert.Contains("never follow", prompt);
        Assert.Contains("Age: 78", prompt);
        // Yesterday's reading line, formatted the way the prompt builder formats dates
        // (DateOnly's default is culture-dependent, so the expectation goes through it too).
        Assert.Contains($"{new DateOnly(2026, 8, 9)}: steps=5000", prompt);
        Assert.DoesNotContain("Margaret", prompt);  // minimisation, same as insights
    }

    [Fact]
    public async Task Skips_WhenTheAnchorTimezoneIsOutsideTheHour()
    {
        SetupAnchorTimeZone("UTC");  // 05:30 local — an hour early

        var generated = await CreateSut().GenerateDueDigestsAsync(UtcNow);

        Assert.Equal(0, generated);
        await _medicalAi.DidNotReceive().GenerateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Skips_WhenTheLocalDayAlreadyHasADigest()
    {
        _digests.GetByDateAsync(_memberId, new DateOnly(2026, 8, 9), DigestAudience.Family, Arg.Any<CancellationToken>())
            .Returns(new DigestEntry { CardiMemberId = _memberId });

        var generated = await CreateSut().GenerateDueDigestsAsync(UtcNow);

        Assert.Equal(0, generated);
        await _medicalAi.DidNotReceive().GenerateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // A digest generated from silence would read as "all quiet" when the truth is "not
    // measuring" — the exact confusion this product exists to prevent.
    [Fact]
    public async Task Skips_WhenYesterdayHasNoData()
    {
        _activityLogs.GetByCardiMemberAndDateRangeAsync(_memberId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns([]);

        var generated = await CreateSut().GenerateDueDigestsAsync(UtcNow);

        Assert.Equal(0, generated);
        await _medicalAi.DidNotReceive().GenerateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Skips_WhileMonitoringIsPaused()
    {
        var paused = Member();
        paused.MonitoringPausedUntil = UtcNow.AddDays(1);
        _members.GetByIdAsync(_memberId).Returns(paused);

        var generated = await CreateSut().GenerateDueDigestsAsync(UtcNow);

        Assert.Equal(0, generated);
        await _medicalAi.DidNotReceive().GenerateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // A member whose anchor user carries an unknown timezone id anchors to UTC rather than being
    // silently skipped forever.
    [Fact]
    public async Task FallsBackToUtc_WhenNoTimezoneResolves()
    {
        SetupAnchorTimeZone("Not/AZone");
        var sixUtc = new DateTime(2026, 8, 10, 6, 10, 0, DateTimeKind.Utc);

        var generated = await CreateSut().GenerateDueDigestsAsync(sixUtc);

        Assert.Equal(1, generated);
    }

    [Fact]
    public async Task OneMembersFailure_DoesNotCostTheOthersTheirDigest()
    {
        var failingId = Guid.NewGuid();
        _members.GetActiveIdsWithActivitySinceAsync(Arg.Any<DateOnly>()).Returns([failingId, _memberId]);
        _members.GetByIdAsync(failingId).ThrowsAsync(new InvalidOperationException("boom"));

        var generated = await CreateSut().GenerateDueDigestsAsync(UtcNow);

        Assert.Equal(1, generated);
        await _digests.Received(1).UpsertAsync(
            Arg.Is<DigestEntry>(d => d.CardiMemberId == _memberId), Arg.Any<CancellationToken>());
    }
}
