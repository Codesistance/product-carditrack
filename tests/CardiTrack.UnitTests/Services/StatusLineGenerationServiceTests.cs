using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// <see cref="StatusLineGenerationService"/> — the batch writer behind the Dashboard hero card's
/// status line. These assertions moved here from the HealthInsightService status suite with the
/// generation itself: the prompt's tier/tone/brevity contract, the headline hygiene, and the
/// don't-generate guards now belong to the writer, while the reader suite keeps only the serving
/// contract.
/// </summary>
public class StatusLineGenerationServiceTests
{
    private readonly IMedicalAiService _medicalAi = Substitute.For<IMedicalAiService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ICardiMemberRepository _members = Substitute.For<ICardiMemberRepository>();
    private readonly IUserCardiMemberRepository _links = Substitute.For<IUserCardiMemberRepository>();
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IAlertRepository _alerts = Substitute.For<IAlertRepository>();
    private readonly IActivityLogRepository _activityLogs = Substitute.For<IActivityLogRepository>();
    private readonly IPatternBaselineRepository _baselines = Substitute.For<IPatternBaselineRepository>();
    private readonly IMemberStatusLineRepository _statusLines = Substitute.For<IMemberStatusLineRepository>();

    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _memberId = Guid.NewGuid();

    public StatusLineGenerationServiceTests()
    {
        _unitOfWork.CardiMembers.Returns(_members);
        _unitOfWork.UserCardiMembers.Returns(_links);
        _unitOfWork.Users.Returns(_users);
        _unitOfWork.Alerts.Returns(_alerts);
        _unitOfWork.ActivityLogs.Returns(_activityLogs);
        _unitOfWork.PatternBaselines.Returns(_baselines);
        _unitOfWork.MemberStatusLines.Returns(_statusLines);

        _members.GetByIdAsync(_memberId).Returns(new CardiMember
        {
            Id = _memberId,
            Name = "Margaret Doe",
            DateOfBirth = new DateOnly(1948, 3, 15),
            IsActive = true,
        });
        _links.GetByCardiMemberIdAsync(_memberId).Returns(
        [
            new UserCardiMember { UserId = _userId, CardiMemberId = _memberId, IsActive = true },
        ]);
        _users.GetByIdAsync(_userId).Returns(new User { Id = _userId, TimeZoneId = "Europe/London" });
        GivenAlerts([]);
        _activityLogs.GetByCardiMemberAndDateRangeAsync(_memberId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns([]);
        _baselines.GetLatestByCardiMemberAsync(_memberId, 30).Returns((PatternBaseline?)null);
        _statusLines.GetByCardiMemberAsync(_memberId).Returns((MemberStatusLine?)null);
        _medicalAi.GenerateStructuredAsync<StatusLineGenerationService.CurrentStatusAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new StatusLineGenerationService.CurrentStatusAiResponse
            {
                Headline = "All steady",
                Message = "Margaret seems steady today.",
            });
    }

    private StatusLineGenerationService CreateSut() =>
        new(_unitOfWork, _medicalAi, PromptContextFactory.Composer(_unitOfWork),
            NullLogger<StatusLineGenerationService>.Instance);

    [Fact]
    public async Task PersistsAFreshRow_WithTheResolvedHeadlineAndMessage()
    {
        await CreateSut().RegenerateAsync(_memberId);

        await _statusLines.Received(1).AddAsync(Arg.Is<MemberStatusLine>(s =>
            s.CardiMemberId == _memberId
            && s.Headline == "All steady"
            && s.Message == "Margaret seems steady today."));
        await _unitOfWork.Received(1).SaveChangesAsync();
    }

    [Fact]
    public async Task UpdatesTheExistingRow_InsteadOfAddingASecond()
    {
        var existing = new MemberStatusLine
        {
            CardiMemberId = _memberId,
            Headline = "Old headline",
            Message = "Old line.",
            GeneratedAtUtc = DateTime.UtcNow.AddHours(-2),
        };
        _statusLines.GetByCardiMemberAsync(_memberId).Returns(existing);

        await CreateSut().RegenerateAsync(_memberId);

        Assert.Equal("All steady", existing.Headline);
        Assert.Equal("Margaret seems steady today.", existing.Message);
        Assert.True(existing.GeneratedAtUtc > DateTime.UtcNow.AddMinutes(-1));
        await _statusLines.DidNotReceive().AddAsync(Arg.Any<MemberStatusLine>());
        await _unitOfWork.Received(1).SaveChangesAsync();
    }

    // An empty answer reads as a transient model hiccup, not a stable "nothing to say" — the
    // previous line (staleness-guarded by the reader) beats no line, so nothing is written.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BlankModelResponse_LeavesTheExistingRowUntouched(string blank)
    {
        var existing = new MemberStatusLine
        {
            CardiMemberId = _memberId,
            Message = "Old line.",
            GeneratedAtUtc = DateTime.UtcNow.AddHours(-2),
        };
        _statusLines.GetByCardiMemberAsync(_memberId).Returns(existing);
        _medicalAi.GenerateStructuredAsync<StatusLineGenerationService.CurrentStatusAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new StatusLineGenerationService.CurrentStatusAiResponse { Message = blank });

        await CreateSut().RegenerateAsync(_memberId);

        Assert.Equal("Old line.", existing.Message);
        await _statusLines.DidNotReceive().AddAsync(Arg.Any<MemberStatusLine>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync();
    }

    /// <summary>
    /// The digest pass and the assessor can regenerate the same member concurrently; both read
    /// no-row, both insert, and the unique index fails the loser. The loser detaches its staged
    /// insert and writes over the winner's row — last writer wins, same as the update path.
    /// </summary>
    [Fact]
    public async Task LosingTheInsertRace_WritesOverTheWinnersRow()
    {
        var winner = new MemberStatusLine
        {
            CardiMemberId = _memberId,
            Message = "Winner line.",
            GeneratedAtUtc = DateTime.UtcNow,
        };
        _statusLines.GetByCardiMemberAsync(_memberId).Returns((MemberStatusLine?)null, winner);
        _unitOfWork.SaveChangesAsync().Returns(
            _ => throw new DbUpdateException("duplicate key value violates unique constraint"),
            _ => 1);

        await CreateSut().RegenerateAsync(_memberId);

        _statusLines.Received(1).Remove(Arg.Is<MemberStatusLine>(s => s.CardiMemberId == _memberId));
        Assert.Equal("Margaret seems steady today.", winner.Message);
        Assert.Equal("All steady", winner.Headline);
        await _unitOfWork.Received(2).SaveChangesAsync();
    }

    /// <summary>
    /// The headline is the punchy note the hero card leads with, so it is a label, not prose: an
    /// answer that arrived quoted or full-stopped is cleaned, and one that ran on into a sentence
    /// is dropped so the dashboard keeps its per-tier headline. The line survives either way.
    /// </summary>
    [Theory]
    [InlineData("\"Quieter than usual.\"", "Quieter than usual")]
    [InlineData("   ", null)]
    [InlineData("Everything about today has looked broadly settled so far, which is reassuring", null)]
    public async Task HeadlineIsCleanedOrDropped_ButNeverCostsTheMessage(string headline, string? expected)
    {
        _medicalAi.GenerateStructuredAsync<StatusLineGenerationService.CurrentStatusAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new StatusLineGenerationService.CurrentStatusAiResponse
            {
                Headline = headline,
                Message = "Margaret seems steady today.",
            });

        await CreateSut().RegenerateAsync(_memberId);

        await _statusLines.Received(1).AddAsync(Arg.Is<MemberStatusLine>(s =>
            s.Headline == expected && s.Message == "Margaret seems steady today."));
    }

    /// <summary>
    /// The headline is asked not to name them, and unlike the sentence it is not resolved on the
    /// way out — a leftover placeholder would be braces in the title, so it is dropped.
    /// </summary>
    [Fact]
    public async Task HeadlineThatStillCarriesThePlaceholder_IsDropped_TheMessageSurvives()
    {
        _medicalAi.GenerateStructuredAsync<StatusLineGenerationService.CurrentStatusAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new StatusLineGenerationService.CurrentStatusAiResponse
            {
                Headline = "CardiTrackCardiMember is quiet",
                Message = "CardiTrackCardiMember seems steady today.",
            });

        await CreateSut().RegenerateAsync(_memberId);

        await _statusLines.Received(1).AddAsync(Arg.Is<MemberStatusLine>(s =>
            s.Headline == null && s.Message == "Margaret seems steady today."));
    }

    [Fact]
    public async Task NoUnresolvedAlerts_SendsGreenAsTheSeverityTier()
    {
        await CreateSut().RegenerateAsync(_memberId);

        var prompt = (string)_medicalAi.ReceivedCalls().Single().GetArguments()[0]!;
        Assert.Contains("--- Current severity tier ---", prompt);
        Assert.Contains("green", prompt);
        Assert.Contains("No unresolved alerts.", prompt);
    }

    [Fact]
    public async Task AFreshYellowAssessment_RaisesTheTierWhenNoAlertsAreOpen()
    {
        var assessments = Substitute.For<IRealtimeAssessmentRepository>();
        _unitOfWork.RealtimeAssessments.Returns(assessments);
        assessments.GetLatestAsync(_memberId, Arg.Any<CancellationToken>()).Returns(new RealtimeAssessment
        {
            CardiMemberId = _memberId,
            Severity = AlertSeverity.Yellow,
            WindowStartUtc = DateTime.UtcNow.AddHours(-2),
        });

        await CreateSut().RegenerateAsync(_memberId);

        var prompt = (string)_medicalAi.ReceivedCalls().Single().GetArguments()[0]!;
        Assert.Contains("yellow", prompt);
        Assert.Contains("No unresolved alerts.", prompt);
    }

    [Fact]
    public async Task TodaysCheckInDigest_RaisesTheTierWhenNoAlertsAreOpen()
    {
        var digests = Substitute.For<IDigestRepository>();
        _unitOfWork.Digests.Returns(digests);
        digests.GetLatestByDateAsync(
                _memberId, Arg.Any<DateOnly>(), DigestAudience.Family, Arg.Any<CancellationToken>())
            .Returns(new DigestEntry
            {
                CardiMemberId = _memberId,
                Audience = DigestAudience.Family,
                Urgency = DigestUrgency.CheckIn,
                Text = "Worth a call today.",
            });

        await CreateSut().RegenerateAsync(_memberId);

        var prompt = (string)_medicalAi.ReceivedCalls().Single().GetArguments()[0]!;
        Assert.Contains("yellow", prompt);
    }

    [Fact]
    public async Task UnresolvedAlerts_SendTheWorstSeverityAndListEachAlert()
    {
        GivenAlerts(
        [
            new Alert
            {
                CardiMemberId = _memberId,
                AlertType = AlertType.PatternBreak,
                Severity = AlertSeverity.Yellow,
                Title = "Steps below baseline",
            },
            new Alert
            {
                CardiMemberId = _memberId,
                AlertType = AlertType.Inactivity,
                Severity = AlertSeverity.Orange,
                Title = "Device has not synced",
            },
        ]);

        await CreateSut().RegenerateAsync(_memberId);

        var prompt = (string)_medicalAi.ReceivedCalls().Single().GetArguments()[0]!;
        Assert.Contains("orange", prompt);
        Assert.Contains("Steps below baseline", prompt);
        Assert.Contains("Device has not synced", prompt);
    }

    /// <summary>
    /// Resolution ends the episode without deactivating the row, so an "active" alert can be one
    /// the assessor already called over — those must not drive the tier.
    /// </summary>
    [Fact]
    public async Task ResolvedAlerts_NoLongerDriveTheTier()
    {
        GivenAlerts(
        [
            new Alert
            {
                CardiMemberId = _memberId,
                AlertType = AlertType.HeartRate,
                Severity = AlertSeverity.Red,
                Title = "Heart rate needs urgent attention",
                IsResolved = true,
            },
        ]);

        await CreateSut().RegenerateAsync(_memberId);

        var prompt = (string)_medicalAi.ReceivedCalls().Single().GetArguments()[0]!;
        Assert.Contains("green", prompt);
        Assert.Contains("No unresolved alerts.", prompt);
        Assert.DoesNotContain("Heart rate needs urgent attention", prompt);
    }

    [Fact]
    public async Task TheTier_ComesFromTheWorstAlertStillUnresolved()
    {
        GivenAlerts(
        [
            new Alert
            {
                CardiMemberId = _memberId,
                AlertType = AlertType.HeartRate,
                Severity = AlertSeverity.Red,
                Title = "Heart rate needs urgent attention",
                IsResolved = true,
            },
            new Alert
            {
                CardiMemberId = _memberId,
                AlertType = AlertType.PatternBreak,
                Severity = AlertSeverity.Yellow,
                Title = "Steps below baseline",
                IsResolved = false,
            },
        ]);

        await CreateSut().RegenerateAsync(_memberId);

        var prompt = (string)_medicalAi.ReceivedCalls().Single().GetArguments()[0]!;
        Assert.Contains("yellow", prompt);
        Assert.Contains("Steps below baseline", prompt);
        Assert.DoesNotContain("Heart rate needs urgent attention", prompt);
    }

    [Fact]
    public async Task TellsTheModelToUseCaregiverLanguageAndStayBrief()
    {
        await CreateSut().RegenerateAsync(_memberId);

        var prompt = (string)_medicalAi.ReceivedCalls().Single().GetArguments()[0]!;
        Assert.Contains("Write as a caregiver would", prompt);
        Assert.Contains("Everyday words for the readings are fine", prompt);
        Assert.Contains("Not clinic-speak", prompt);
        Assert.Contains("enough to be informed and react, not to treat or fix", prompt);
        Assert.DoesNotContain("heart rate, sleep, quieter today, worth a look", prompt);
        Assert.DoesNotContain("a bug", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("poor night", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Never suggest a medical cause", prompt);
        Assert.Contains("never diagnose", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("under 15 words", prompt);
        Assert.Contains("green settled, yellow a mention", prompt);
        Assert.Contains("write CardiTrackCardiMember exactly as written", prompt);
        Assert.DoesNotContain("Never use clinical terms", prompt, StringComparison.Ordinal);
    }

    // ── Prompt size ─────────────────────────────────────────────────────────────
    //
    // The batch pays for this prompt on every digest regeneration, so instruction length is still
    // inference volume even though no caregiver waits on it any more.

    [Fact]
    public async Task TheRenderedPrompt_StaysWorthPayingFor()
    {
        await CreateSut().RegenerateAsync(_memberId);

        var prompt = (string)_medicalAi.ReceivedCalls().Single().GetArguments()[0]!;

        Assert.True(prompt.Length < 2_000,
            $"The status prompt renders to {prompt.Length} characters; the batch pays for every "
            + "one on each digest regeneration.");
    }

    [Fact]
    public void TheFixedInstructions_StayWithinTheirBudget()
    {
        Assert.True(
            StatusLineGenerationService.CurrentStatusInstructionsLength
                <= StatusLineGenerationService.StatusPromptBudget,
            $"The status instructions are {StatusLineGenerationService.CurrentStatusInstructionsLength} "
            + $"characters against a budget of {StatusLineGenerationService.StatusPromptBudget}.");
    }

    // ── Not spending a model call ───────────────────────────────────────────────

    [Fact]
    public async Task PausedMember_IsNeverGeneratedFor()
    {
        _members.GetByIdAsync(_memberId).Returns(new CardiMember
        {
            Id = _memberId,
            Name = "Margaret Doe",
            IsActive = true,
            MonitoringPausedUntil = DateTime.UtcNow.AddHours(4),
        });

        await CreateSut().RegenerateAsync(_memberId);

        await _medicalAi.DidNotReceive().GenerateStructuredAsync<StatusLineGenerationService.CurrentStatusAiResponse>(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _statusLines.DidNotReceive().AddAsync(Arg.Any<MemberStatusLine>());
    }

    /// <summary>A pause that has elapsed is not a pause — the member is being watched again.</summary>
    [Fact]
    public async Task ExpiredPause_StillGenerates()
    {
        _members.GetByIdAsync(_memberId).Returns(new CardiMember
        {
            Id = _memberId,
            Name = "Margaret Doe",
            IsActive = true,
            MonitoringPausedUntil = DateTime.UtcNow.AddHours(-1),
        });

        await CreateSut().RegenerateAsync(_memberId);

        await _statusLines.Received(1).AddAsync(Arg.Is<MemberStatusLine>(s =>
            s.Message == "Margaret seems steady today."));
    }

    [Fact]
    public async Task InactiveMember_IsNeverGeneratedFor()
    {
        _members.GetByIdAsync(_memberId).Returns(new CardiMember
        {
            Id = _memberId,
            Name = "Margaret Doe",
            IsActive = false,
        });

        await CreateSut().RegenerateAsync(_memberId);

        await _medicalAi.DidNotReceive().GenerateStructuredAsync<StatusLineGenerationService.CurrentStatusAiResponse>(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _statusLines.DidNotReceive().AddAsync(Arg.Any<MemberStatusLine>());
    }

    /// <summary>
    /// Stubs the unresolved read the generator makes. That query filters IsActive and !IsResolved
    /// in SQL and orders newest first, so the fake hands back only what the caller says is open,
    /// in the order the repository would — which is what keeps a test that passes a resolved alert
    /// still proving the generator ignores it.
    /// </summary>
    private void GivenAlerts(Alert[] alerts) =>
        _alerts.GetUnresolvedByCardiMemberAsync(_memberId).Returns(
            alerts.Where(a => a.IsActive && !a.IsResolved)
                .OrderByDescending(a => a.TriggeredDate)
                .ToList());

}
