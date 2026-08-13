using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Services.PromptContext;
using NSubstitute;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// What each context source does and does not put in front of the model.
/// </summary>
public class MemberContextSourceTests
{
    private static readonly DateTime UtcNow = new(2026, 8, 13, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Today = new(2026, 8, 13);
    private readonly Guid _memberId = Guid.NewGuid();

    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IEnvironmentalReadingRepository _environmental =
        Substitute.For<IEnvironmentalReadingRepository>();
    private readonly IRealtimeAssessmentRepository _assessments =
        Substitute.For<IRealtimeAssessmentRepository>();
    private readonly IAlertRepository _alerts = Substitute.For<IAlertRepository>();
    private readonly IMemberQuestionnaireRepository _questionnaires =
        Substitute.For<IMemberQuestionnaireRepository>();

    public MemberContextSourceTests()
    {
        _unitOfWork.EnvironmentalReadings.Returns(_environmental);
        _unitOfWork.RealtimeAssessments.Returns(_assessments);
        _unitOfWork.Alerts.Returns(_alerts);
        _unitOfWork.MemberQuestionnaires.Returns(_questionnaires);

        _assessments.GetSinceAsync(_memberId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _alerts.GetByCardiMemberAsync(_memberId, Arg.Any<bool>()).Returns([]);
        _questionnaires.GetByCardiMemberAsync(_memberId, Arg.Any<CancellationToken>()).Returns([]);
    }

    private MemberContextRequest Request(
        CardiMember? member = null, PromptPurpose purpose = PromptPurpose.Digest) =>
        new(member ?? new CardiMember { Id = _memberId }, _memberId, Today, UtcNow, purpose);

    // ---- Demographics ----

    /// <summary>
    /// The bug this source exists to close: MedicalNotes is stored encrypted, and every prompt used
    /// to pass the stored column straight through — so the model read a "v1:…" envelope where the
    /// caregiver's note about conditions and medication should have been.
    /// </summary>
    [Fact]
    public async Task Demographics_PutsTheCaregiverNoteInThePrompt_NotItsCiphertext()
    {
        var note = "Diagnosed with atrial fibrillation in 2019. Lives alone.";
        var member = new CardiMember
        {
            Id = _memberId,
            DateOfBirth = new DateOnly(1948, 3, 2),
            Gender = Gender.Female,
            MedicalNotes = PromptContextFactory.Encryption.Encrypt(note),
        };

        var section = await new DemographicsContextSource(PromptContextFactory.Encryption)
            .BuildAsync(Request(member), default);

        Assert.NotNull(section);
        Assert.Contains(note, section.Body);
        Assert.DoesNotContain("v1:", section.Body);
        Assert.Contains("Age: 78", section.Body);
        Assert.Contains("Sex: Female", section.Body);
    }

    /// <summary>Notes written before the column was encrypted still read back as what was typed.</summary>
    [Fact]
    public async Task Demographics_FallsBackToAPlainTextNote_WrittenBeforeEncryption()
    {
        var member = new CardiMember
        {
            Id = _memberId,
            DateOfBirth = new DateOnly(1950, 1, 1),
            MedicalNotes = "Uses a walking frame indoors.",
        };

        var section = await new DemographicsContextSource(PromptContextFactory.Encryption)
            .BuildAsync(Request(member), default);

        Assert.Contains("Uses a walking frame indoors.", section!.Body);
    }

    // ---- Environmental ----

    /// <summary>
    /// Consent is the gate, and it is checked before the query — most members have not granted it,
    /// so the common case must not cost a roundtrip.
    /// </summary>
    [Fact]
    public async Task Environmental_WithoutConsent_IsSilent_AndNeverQueries()
    {
        var section = await new EnvironmentalContextSource(_unitOfWork)
            .BuildAsync(Request(new CardiMember { Id = _memberId }), default);

        Assert.Null(section);
        await _environmental.DidNotReceive().GetLatestAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Environmental_DescribesTheConditionsAndHowLongAgo()
    {
        GivenReading(UtcNow.AddHours(-2), temperature: 24.4, condition: "Light rain", humidity: 78, category: "Good");

        var section = await new EnvironmentalContextSource(_unitOfWork)
            .BuildAsync(Request(ConsentedMember()), default);

        Assert.NotNull(section);
        Assert.Contains("24°C", section.Body);
        Assert.Contains("Light rain", section.Body);
        Assert.Contains("humidity 78%", section.Body);
        Assert.Contains("air quality Good", section.Body);
        Assert.Contains("2h ago", section.Body);
    }

    /// <summary>
    /// The assessor reads one hour and must not blame this hour's heart rate on yesterday's heat;
    /// a daily summary can honestly mention a session further back. Same reading, different answer.
    /// </summary>
    [Theory]
    [InlineData(PromptPurpose.RealtimeAssessment, 2, true)]
    [InlineData(PromptPurpose.RealtimeAssessment, 4, false)]
    [InlineData(PromptPurpose.CurrentStatus, 5, true)]
    [InlineData(PromptPurpose.CurrentStatus, 7, false)]
    [InlineData(PromptPurpose.Digest, 20, true)]
    [InlineData(PromptPurpose.Digest, 26, false)]
    [InlineData(PromptPurpose.BaselineInsight, 40, true)]
    [InlineData(PromptPurpose.BaselineInsight, 50, false)]
    public async Task Environmental_StalenessIsPerPrompt(PromptPurpose purpose, int hoursAgo, bool expected)
    {
        GivenReading(UtcNow.AddHours(-hoursAgo), temperature: 18);

        var section = await new EnvironmentalContextSource(_unitOfWork)
            .BuildAsync(Request(ConsentedMember(), purpose), default);

        Assert.Equal(expected, section is not null);
    }

    [Fact]
    public async Task Environmental_WithNothingTheLookupCouldFind_IsSilent()
    {
        GivenReading(UtcNow.AddHours(-1));

        Assert.Null(await new EnvironmentalContextSource(_unitOfWork)
            .BuildAsync(Request(ConsentedMember()), default));
    }

    // ---- Monitoring ----

    /// <summary>
    /// A calm member's prompt carries no monitoring section at all, which is a stronger guarantee
    /// than instructing the model not to mention monitoring: the words are not there to echo.
    /// </summary>
    [Fact]
    public async Task Monitoring_WithNothingNotable_IsSilent()
    {
        Assert.Null(await new MonitoringContextSource(_unitOfWork).BuildAsync(Request(), default));
    }

    [Fact]
    public async Task Monitoring_CarriesUnresolvedAlertsAndNotableAssessments()
    {
        GivenAssessments(
            Assessment(AlertSeverity.Yellow, "Resting heart rate has been a little higher since lunchtime."));
        GivenAlerts(new Alert
        {
            CardiMemberId = _memberId,
            AlertType = AlertType.HeartRate,
            Severity = AlertSeverity.Orange,
            Title = "Heart rate worth checking on",
            TriggeredDate = UtcNow.AddHours(-3),
            IsResolved = false,
        });

        var section = await new MonitoringContextSource(_unitOfWork).BuildAsync(Request(), default);

        Assert.NotNull(section);
        Assert.Equal("Recent monitoring context", section.Label);
        Assert.Contains("Unresolved alert (Orange, HeartRate, raised 3h ago): Heart rate worth checking on", section.Body);
        Assert.Contains("Resting heart rate has been a little higher", section.Body);
    }

    /// <summary>
    /// Green is "all is well" — the summary says that anyway when nothing else is here to say.
    /// Yellow is Medium on the pipeline's scale, the tier the assessor's prompt promises to surface.
    /// </summary>
    [Fact]
    public async Task Monitoring_IgnoresAssessmentsBelowYellow()
    {
        GivenAssessments(Assessment(AlertSeverity.Green, "All quiet."));

        Assert.Null(await new MonitoringContextSource(_unitOfWork).BuildAsync(Request(), default));
    }

    /// <summary>
    /// Resolution keeps IsActive set — the episode ended, the row did not. Reading a closed episode
    /// as live is what had the digest and the hero line disagreeing with the status colour.
    /// </summary>
    [Fact]
    public async Task Monitoring_IgnoresResolvedAlerts()
    {
        GivenAlerts(new Alert
        {
            CardiMemberId = _memberId,
            AlertType = AlertType.HeartRate,
            Severity = AlertSeverity.Red,
            Title = "Heart rate needs urgent attention",
            TriggeredDate = UtcNow.AddHours(-6),
            IsResolved = true,
        });

        Assert.Null(await new MonitoringContextSource(_unitOfWork).BuildAsync(Request(), default));
    }

    [Fact]
    public async Task Monitoring_CarriesAtMostFiveAssessments()
    {
        GivenAssessments(Enumerable.Range(0, 9)
            .Select(i => Assessment(AlertSeverity.Yellow, $"Observation {i}"))
            .ToArray());

        var section = await new MonitoringContextSource(_unitOfWork).BuildAsync(Request(), default);

        Assert.Equal(5, section!.Body.Split('\n').Length);
    }

    // ---- Questionnaire answers ----

    [Fact]
    public async Task Answers_WithNoneGiven_IsSilent()
    {
        Assert.Null(await new QuestionnaireAnswersContextSource(_unitOfWork, PromptContextFactory.Encryption)
            .BuildAsync(Request(), default));
    }

    [Fact]
    public async Task Answers_CarryTheQuestionAndTheAnswer_Decrypted()
    {
        GivenQuestionnaires(
            Questionnaire(QuestionnaireStatus.Answered, "Has anything changed at home recently?", "She moved bedrooms last week."),
            Questionnaire(QuestionnaireStatus.Pending, "Is she sleeping later than usual?", null),
            Questionnaire(QuestionnaireStatus.Dismissed, "Any visitors this week?", null));

        var section = await new QuestionnaireAnswersContextSource(_unitOfWork, PromptContextFactory.Encryption)
            .BuildAsync(Request(), default);

        Assert.NotNull(section);
        Assert.Contains("Q: Has anything changed at home recently? A: She moved bedrooms last week.", section.Body);
        Assert.DoesNotContain("v1:", section.Body);
        // Only the answered one: an unanswered question tells the model nothing, and a dismissed
        // one is the family having declined to say.
        Assert.Single(section.Body.Split('\n'));
    }

    [Fact]
    public async Task Answers_CarryAtMostThree_NewestFirst()
    {
        GivenQuestionnaires(Enumerable.Range(0, 6)
            .Select(i => Questionnaire(QuestionnaireStatus.Answered, $"Question {i}?", $"Answer {i}"))
            .ToArray());

        var section = await new QuestionnaireAnswersContextSource(_unitOfWork, PromptContextFactory.Encryption)
            .BuildAsync(Request(), default);

        Assert.Equal(3, section!.Body.Split('\n').Length);
        Assert.Contains("Answer 0", section.Body);
        Assert.DoesNotContain("Answer 4", section.Body);
    }

    // ---- Arrangement helpers ----

    private CardiMember ConsentedMember() => new()
    {
        Id = _memberId,
        DateOfBirth = new DateOnly(1946, 6, 6),
        EnvironmentalContextConsentGranted = true,
    };

    private void GivenReading(
        DateTime sessionEndUtc, double? temperature = null, string? condition = null,
        int? humidity = null, string? category = null)
    {
        _environmental.GetLatestAsync(_memberId, Arg.Any<CancellationToken>()).Returns(new EnvironmentalReading
        {
            CardiMemberId = _memberId,
            SessionStartUtc = sessionEndUtc.AddMinutes(-40),
            SessionEndUtc = sessionEndUtc,
            TemperatureCelsius = temperature,
            WeatherCondition = condition,
            RelativeHumidityPercent = humidity,
            AirQualityCategory = category,
        });
    }

    private void GivenAssessments(params RealtimeAssessment[] assessments) =>
        _assessments.GetSinceAsync(_memberId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(assessments);

    private void GivenAlerts(params Alert[] alerts) =>
        _alerts.GetByCardiMemberAsync(_memberId, Arg.Any<bool>()).Returns(alerts);

    private void GivenQuestionnaires(params MemberQuestionnaire[] questionnaires) =>
        _questionnaires.GetByCardiMemberAsync(_memberId, Arg.Any<CancellationToken>())
            .Returns(questionnaires);

    private RealtimeAssessment Assessment(AlertSeverity severity, string message) => new()
    {
        CardiMemberId = _memberId,
        WindowStartUtc = UtcNow.AddHours(-2),
        WindowEndUtc = UtcNow.AddHours(-1),
        Severity = severity,
        ModelOutput = message,
        GeneratedAtUtc = UtcNow.AddHours(-1),
    };

    private MemberQuestionnaire Questionnaire(QuestionnaireStatus status, string question, string? answer) => new()
    {
        Id = Guid.NewGuid(),
        CardiMemberId = _memberId,
        QuestionText = PromptContextFactory.Encryption.Encrypt(question),
        AnswerText = answer is null ? null : PromptContextFactory.Encryption.Encrypt(answer),
        Status = status,
        GeneratedAtUtc = UtcNow.AddDays(-1),
    };
}
