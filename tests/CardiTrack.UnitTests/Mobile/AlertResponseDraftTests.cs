using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Core.Alerts;

namespace CardiTrack.UnitTests.Mobile;

public class AlertResponseDraftTests
{
    [Fact]
    public void EmptyDraftCannotBeSent()
    {
        var draft = new AlertResponseDraft();

        Assert.False(draft.CanSubmit);
        Assert.True(draft.IsEmpty);
        Assert.Equal("0/500", draft.Counter);
    }

    [Fact]
    public void ACannedPickAloneIsEnough_AndSoIsANoteAlone()
    {
        Assert.True(new AlertResponseDraft { Code = "calling" }.CanSubmit);
        Assert.True(new AlertResponseDraft { Note = "On my way" }.CanSubmit);
        Assert.False(new AlertResponseDraft { Note = "   " }.CanSubmit);
    }

    [Fact]
    public void TheCounterAndTheLimitReadTheSameNumber()
    {
        var draft = new AlertResponseDraft { Note = new string('x', 500) };

        Assert.Equal("500/500", draft.Counter);
        Assert.False(draft.IsOverLimit);
        draft.Note += "y";
        Assert.True(draft.IsOverLimit);
    }

    [Fact]
    public void ToRequest_TrimsTheNote_AndSendsNullForNothing()
    {
        var request = new AlertResponseDraft { Code = "aware", Note = "  she's fine  " }.ToRequest();

        Assert.Equal("aware", request.ResponseCode);
        Assert.Equal("she's fine", request.Note);
        Assert.Null(new AlertResponseDraft { Code = "aware" }.ToRequest().Note);
    }

    [Fact]
    public void ADraftSurvivesTheRoundTripThroughStorage()
    {
        var draft = new AlertResponseDraft { Code = "calling", Note = "Rang twice, no answer\nTrying the neighbour" };

        var restored = AlertResponseDraft.Deserialize(draft.Serialize());

        Assert.NotNull(restored);
        Assert.Equal("calling", restored.Code);
        Assert.Equal(draft.Note, restored.Note);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    public void UnreadableStorageIsNoDraft(string? stored)
    {
        Assert.Null(AlertResponseDraft.Deserialize(stored));
    }

    [Fact]
    public void OptionsFor_PicksTheListForTheKind()
    {
        var alert = new AlertDetailResponse
        {
            ResponseOptions = new AlertResponseOptionsResponse
            {
                Acknowledge = [new() { Code = "calling", Label = "Calling them now" }],
                Close = [new() { Code = "expected", Label = "Expected, nothing wrong" }],
            },
        };

        Assert.Equal("calling", AlertAnswerKinds.OptionsFor(alert, AlertAnswerKind.Acknowledge).Single().Code);
        Assert.Equal("expected", AlertAnswerKinds.OptionsFor(alert, AlertAnswerKind.Close).Single().Code);
        // "Resolve" to the caregiver, "close" on the wire.
        Assert.Equal("Resolve", AlertAnswerKinds.ButtonText(AlertAnswerKind.Close));
        Assert.Equal("Resolve this alert", AlertAnswerKinds.Title(AlertAnswerKind.Close));
        Assert.Equal("close", AlertAnswerKinds.Wire(AlertAnswerKind.Close));
        Assert.Equal(AlertAnswerKind.Close, AlertAnswerKinds.Parse("close"));
        Assert.Null(AlertAnswerKinds.Parse("undo"));
    }
}
