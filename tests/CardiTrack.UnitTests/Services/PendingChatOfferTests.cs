using CardiTrack.Application.DTOs.Common;
using CardiTrack.Application.Services;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// The follow-up a chat answer ends by offering: which reading it names, what the offer and the
/// question a yes becomes both say, and when a stored offer still counts.
/// </summary>
public class PendingChatOfferTests
{
    private static readonly DateTime Now = new(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(ChartMetricKind.Sleep, ChartMetricKind.RestingHeartRate)]
    [InlineData(ChartMetricKind.RestingHeartRate, ChartMetricKind.Sleep)]
    [InlineData(ChartMetricKind.Steps, ChartMetricKind.Sleep)]
    [InlineData(ChartMetricKind.HeartRateVariability, ChartMetricKind.RestingHeartRate)]
    [InlineData(ChartMetricKind.OvernightBreathingRate, ChartMetricKind.Sleep)]
    public void EachAnsweredReading_OffersItsNeighbour(ChartMetricKind answered, ChartMetricKind offered) =>
        Assert.Equal(offered, PendingChatOffer.For(answered, 7, Now)!.Metric);

    /// <summary>Chat reads a week at most, so nothing wider is ever offered.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(30)]
    public void AWindowChatCannotRead_IsOfferedNothing(int days) =>
        Assert.Null(PendingChatOffer.For(ChartMetricKind.Sleep, days, Now));

    [Fact]
    public void TheOffer_NamesTheMember_AndTheSameDays()
    {
        var offer = PendingChatOffer.For(ChartMetricKind.Sleep, 7, Now)!;

        Assert.Equal("Would you like me to look at Pop's resting heart rate over the same days too?", offer.Sentence("Pop"));
    }

    [Theory]
    [InlineData(7, "How has CardiTrackCardiMember's resting heart rate been this week?")]
    [InlineData(3, "How has CardiTrackCardiMember's resting heart rate been over the last 3 days?")]
    [InlineData(1, "How has CardiTrackCardiMember's resting heart rate been over the last day?")]
    public void AYes_BecomesAQuestionOverTheSameDays(int days, string question) =>
        Assert.Equal(question, PendingChatOffer.For(ChartMetricKind.Sleep, days, Now)!.Question("CardiTrackCardiMember"));

    [Fact]
    public void ASleepOffer_AsksHowTheySlept() =>
        Assert.Equal(
            "How has CardiTrackCardiMember slept this week?",
            PendingChatOffer.For(ChartMetricKind.RestingHeartRate, 7, Now)!.Question("CardiTrackCardiMember"));

    [Fact]
    public void AnOffer_LapsesAfterItsWindow()
    {
        var offer = PendingChatOffer.For(ChartMetricKind.Sleep, 7, Now)!;

        Assert.True(offer.IsCurrent(Now + PendingChatOffer.Validity));
        Assert.False(offer.IsCurrent(Now + PendingChatOffer.Validity + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void AStoredOffer_RoundTrips()
    {
        var offer = PendingChatOffer.For(ChartMetricKind.Steps, 5, Now)!;

        Assert.Equal(offer, PendingChatOffer.FromJson(offer.ToJson()));
    }

    /// <summary>Anything code could not have written is no offer, never one coerced into shape.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"Metric":"Steps","Days":7,"OfferedAtUtc":"2026-09-26T12:00:00Z"}""")]
    [InlineData("""{"Metric":"Sleep","Days":30,"OfferedAtUtc":"2026-09-26T12:00:00Z"}""")]
    [InlineData("""{"Metric":"Sleep","Days":0,"OfferedAtUtc":"2026-09-26T12:00:00Z"}""")]
    [InlineData("""{"Metric":"Nonsense","Days":7,"OfferedAtUtc":"2026-09-26T12:00:00Z"}""")]
    public void AnOfferCodeCouldNotHaveMade_IsNoOffer(string? json) =>
        Assert.Null(PendingChatOffer.FromJson(json));

    [Fact]
    public void TheOffer_ClosesTheAnswer_AheadOfItsReferences()
    {
        var reply = MemberChatReplies.WithOffer(
            "Pop's week looks settled.\n\nReferences: NSF sleep duration.", "Would you like more?", 4_000);

        Assert.Equal("Pop's week looks settled. Would you like more?\n\nReferences: NSF sleep duration.", reply);
    }

    /// <summary>An offer is never worth trimming the answer for.</summary>
    [Fact]
    public void AnOfferThatDoesNotFit_IsLeftOff() =>
        Assert.Null(MemberChatReplies.WithOffer(new string('a', 100), "Would you like more?", 110));

    [Theory]
    [InlineData(ConfirmationAnswer.Yes, "Pop", "Happy to help. What would you like to know about Pop?")]
    [InlineData(ConfirmationAnswer.Yes, null, "Happy to help. What would you like to know?")]
    [InlineData(ConfirmationAnswer.No, "Pop", "No problem. Ask me whenever you'd like to know anything about Pop.")]
    public void ABareAnswerWithNothingPending_AsksWhatTheyWouldLike(ConfirmationAnswer answer, string? name, string expected) =>
        Assert.Equal(expected, MemberChatReplies.NothingPendingReply(answer, name));
}
