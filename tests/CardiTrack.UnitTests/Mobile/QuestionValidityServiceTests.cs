using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Core.Api;
using CardiTrack.Mobile.Core.Questionnaires;
using NSubstitute;

namespace CardiTrack.UnitTests.Mobile;

/// <summary>
/// The app's half of retiring a question that outlived its day: stop drawing the card, and tell the
/// server so the row stops blocking the next question.
/// </summary>
public class QuestionValidityServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 17, 7, 15, 0, TimeSpan.Zero);

    private readonly ICardiTrackApiClient _api = Substitute.For<ICardiTrackApiClient>();
    private readonly FakeClock _clock = new(Now);

    private QuestionValidityService CreateSut() => new(_api, _clock);

    private static QuestionnaireResponse Question(DateTime? askableUntilUtc) => new()
    {
        Id = Guid.NewGuid(),
        QuestionText = "Did he feel tired at all today?",
        Status = "pending",
        AskableUntilUtc = askableUntilUtc,
    };

    [Fact]
    public void PassesThroughAQuestionStillWorthAsking_AndTellsTheServerNothing()
    {
        var live = Question(Now.UtcDateTime.AddHours(4));

        Assert.Same(live, CreateSut().Verify(live));

        _api.DidNotReceiveWithAnyArgs().ExpireQuestionnaireAsync(default);
    }

    [Fact]
    public void PassesNullThrough_SoCallersDoNotCheckTwice()
    {
        Assert.Null(CreateSut().Verify(null));
    }

    [Fact]
    public async Task DropsALapsedQuestion_AndAsksTheServerToRetireIt()
    {
        var lapsed = Question(Now.UtcDateTime.AddHours(-5));

        Assert.Null(CreateSut().Verify(lapsed));

        // Started but not awaited by the caller, which is the point — the screen does not wait on a
        // tidy-up. Draining it here is what makes the call observable.
        await Task.Yield();
        await _api.Received(1).ExpireQuestionnaireAsync(lapsed.Id);
    }

    /// <summary>
    /// Both surfaces load the same pending question and the member's page reloads whenever it
    /// appears — without this, one lapsed card would send a retirement on every load.
    /// </summary>
    [Fact]
    public async Task TellsTheServerOnce_HoweverManyScreensNoticeTheSameQuestion()
    {
        var lapsed = Question(Now.UtcDateTime.AddHours(-5));
        var sut = CreateSut();

        sut.Verify(lapsed);
        await Task.Yield();
        sut.Verify(lapsed);
        sut.Verify(lapsed);
        await Task.Yield();

        await _api.Received(1).ExpireQuestionnaireAsync(lapsed.Id);
    }

    /// <summary>
    /// A phone with no signal has nothing to tell the user here: the card is already gone, the
    /// server's own sweep retires the row anyway, and the retry costs one call on the next load.
    /// </summary>
    [Fact]
    public async Task SwallowsAFailedRetirement_AndRetriesItNextTime()
    {
        var lapsed = Question(Now.UtcDateTime.AddHours(-5));
        _api.ExpireQuestionnaireAsync(lapsed.Id)
            .Returns(Task.FromException<QuestionnaireResponse>(
                new ApiException(System.Net.HttpStatusCode.ServiceUnavailable, "No connection.")));

        var sut = CreateSut();

        Assert.Null(sut.Verify(lapsed));
        await Task.Yield();
        Assert.Null(sut.Verify(lapsed));
        await Task.Yield();

        await _api.Received(2).ExpireQuestionnaireAsync(lapsed.Id);
    }

    /// <summary>
    /// Fire-and-forget must observe every failure, not only the typed transport ones — an
    /// unexpected exception would otherwise be unobserved and never clear the "already reported"
    /// set for a retry.
    /// </summary>
    [Fact]
    public async Task SwallowsAnUnexpectedRetirementFailure_AndRetriesItNextTime()
    {
        var lapsed = Question(Now.UtcDateTime.AddHours(-5));
        _api.ExpireQuestionnaireAsync(lapsed.Id)
            .Returns(Task.FromException<QuestionnaireResponse>(
                new InvalidOperationException("Unexpected client fault.")));

        var sut = CreateSut();

        Assert.Null(sut.Verify(lapsed));
        await Task.Yield();
        Assert.Null(sut.Verify(lapsed));
        await Task.Yield();

        await _api.Received(2).ExpireQuestionnaireAsync(lapsed.Id);
    }

    /// <summary>
    /// The one case that decides between a card the family can still answer and one they cannot:
    /// the question is live when the screen opens and lapses while the editor is open.
    /// </summary>
    [Fact]
    public void ReadsTheClockEachTime_SoAQuestionCanLapseWhileTheScreenIsOpen()
    {
        var question = Question(Now.UtcDateTime.AddHours(1));
        var sut = CreateSut();

        Assert.NotNull(sut.Verify(question));

        _clock.Advance(TimeSpan.FromHours(2));

        Assert.Null(sut.Verify(question));
    }

    private sealed class FakeClock(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _now = utcNow;

        public void Advance(TimeSpan by) => _now = _now.Add(by);

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
