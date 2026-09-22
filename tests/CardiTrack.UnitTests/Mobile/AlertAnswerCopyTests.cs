using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Mobile.Core.Alerts;

namespace CardiTrack.UnitTests.Mobile;

public class AlertAnswerCopyTests
{
    private static AlertResponseEntry Response(string kind, string who, string? label, string? note, int minutesAgo) => new()
    {
        Id = Guid.NewGuid(),
        Kind = kind,
        UserId = Guid.NewGuid(),
        UserName = who,
        ResponseLabel = label,
        Note = note,
        CreatedAt = DateTime.UtcNow.AddMinutes(-minutesAgo),
    };

    [Fact]
    public void AnOpenAlertHasNoHandledLine()
    {
        Assert.Null(AlertAnswerCopy.HandledLine(new AlertDetailResponse { Status = "new" }));
    }

    [Fact]
    public void TheLatestResponseLeads_WithItsLabel()
    {
        var alert = new AlertDetailResponse
        {
            Status = "acknowledged",
            AcknowledgedAt = DateTime.UtcNow.AddMinutes(-20),
            AcknowledgedByName = "Jane Doe",
            Responses =
            [
                Response("acknowledge", "Jane Doe", "I know about this", null, 20),
                Response("acknowledge", "Tom Doe", "Calling them now", "Rang twice", 2),
            ],
        };

        var line = AlertAnswerCopy.HandledLine(alert);

        Assert.StartsWith("Tom acknowledged — Calling them now, 2 minutes ago", line);
    }

    [Fact]
    public void ACaregiverCloseSaysWhoClosedIt()
    {
        var alert = new AlertDetailResponse
        {
            Status = "resolved",
            ResolvedByUserId = Guid.NewGuid(),
            ResolvedByName = "Jane Doe",
            Responses = [Response("close", "Jane Doe", "Spoke to them — they're fine", null, 1)],
        };

        Assert.StartsWith("Jane closed this — Spoke to them — they're fine", AlertAnswerCopy.HandledLine(alert));
    }

    [Fact]
    public void ASystemResolutionWithNoResponsesSaysItSettledOnItsOwn()
    {
        var alert = new AlertDetailResponse { Status = "resolved" };

        Assert.Equal(AlertAnswerCopy.SettledOnItsOwn, AlertAnswerCopy.HandledLine(alert));
    }

    [Fact]
    public void ANoteOnAnAlertTheSystemHadAlreadyResolvedSaysBoth()
    {
        var alert = new AlertDetailResponse
        {
            Status = "resolved",
            Responses = [Response("close", "Jane Doe", "Expected, nothing wrong", null, 1)],
        };

        var line = AlertAnswerCopy.HandledLine(alert);

        Assert.StartsWith("Jane closed this — Expected, nothing wrong", line);
        Assert.EndsWith("It had already settled on its own.", line);
    }

    [Fact]
    public void TheBareAcknowledgementStillReadsAsBefore()
    {
        var alert = new AlertDetailResponse
        {
            Status = "acknowledged",
            AcknowledgedAt = DateTime.UtcNow.AddMinutes(-5),
            AcknowledgedByName = "Sam Smith",
        };

        Assert.Equal("Acknowledged by Sam, 5 minutes ago", AlertAnswerCopy.HandledLine(alert));
    }

    [Fact]
    public void RowsReadWhoDidWhat_AndCarryTheNoteUnderTheLabel()
    {
        var row = Response("close", "Tom Doe", "Dealt with another way", "Neighbour popped in", 3);

        Assert.Equal("Tom closed this", AlertAnswerCopy.RowTitle(row));
        Assert.Equal("Dealt with another way\nNeighbour popped in", AlertAnswerCopy.RowDetail(row));
    }

    [Fact]
    public void ARetiredCodeShowsTheNoteAlone_AndABareTapStillHasARow()
    {
        Assert.Equal("Just the note", AlertAnswerCopy.RowDetail(Response("acknowledge", "Tom", null, "Just the note", 1)));
        Assert.Equal("No details given", AlertAnswerCopy.RowDetail(Response("acknowledge", "Tom", null, null, 1)));
        Assert.Equal("Someone acknowledged", AlertAnswerCopy.RowTitle(Response("acknowledge", "", null, null, 1)));
    }

    [Fact]
    public void NewestFirst_DoesNotTrustTheServersOrder()
    {
        var older = Response("acknowledge", "A", null, null, 10);
        var newer = Response("close", "B", null, null, 1);

        var ordered = AlertAnswerCopy.NewestFirst([older, newer]);

        Assert.Same(newer, ordered[0]);
    }
}
