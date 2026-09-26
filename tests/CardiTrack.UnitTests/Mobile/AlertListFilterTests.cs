using CardiTrack.Mobile.Core.Alerts;

namespace CardiTrack.UnitTests.Mobile;

public class AlertListFilterTests
{
    private static readonly DateTime Today = new(2026, 9, 26, 0, 0, 0, DateTimeKind.Local);
    private static readonly Guid Pop = Guid.NewGuid();

    [Fact]
    public void None_AsksForEveryOpenAlert_AndNarrowsNothing()
    {
        var (severity, status, from) = AlertListFilter.None.ToQuery(Today, archived: false);

        Assert.Null(severity);
        Assert.Equal("open", status);
        Assert.Null(from);
        Assert.Equal(0, AlertListFilter.None.ActiveCount(archived: false));
        Assert.Empty(AlertListFilter.None.Parts(archived: false));
    }

    [Fact]
    public void EveryPart_CombinesIntoOneQuery()
    {
        var filter = new AlertListFilter(Pop, "Pop", AlertStatusChoice.NeedsResponse, AlertSeverityChoice.Critical, AlertWindow.Last7Days);

        var (severity, status, from) = filter.ToQuery(Today, archived: false);

        Assert.Equal("red", severity);
        Assert.Equal("new", status);
        Assert.Equal(new DateTime(2026, 9, 20), from);
        Assert.Equal(4, filter.ActiveCount(archived: false));
    }

    [Theory]
    [InlineData(AlertSeverityChoice.Critical, "red")]
    [InlineData(AlertSeverityChoice.Urgent, "orange")]
    [InlineData(AlertSeverityChoice.Notice, "yellow")]
    [InlineData(AlertSeverityChoice.Info, "green")]
    [InlineData(AlertSeverityChoice.Any, null)]
    public void Severity_UsesTheWireColour(AlertSeverityChoice choice, string? wire) =>
        Assert.Equal(wire, (AlertListFilter.None with { Severity = choice }).ToQuery(Today, false).Severity);

    [Theory]
    [InlineData(AlertWindow.Today, 26)]
    [InlineData(AlertWindow.Last7Days, 20)]
    [InlineData(AlertWindow.Last30Days, 28)]
    public void Window_CountsWholeLocalDaysIncludingToday(AlertWindow window, int expectedDay)
    {
        var from = (AlertListFilter.None with { Window = window }).ToQuery(Today, false).From!.Value;

        Assert.Equal(expectedDay, from.Day);
        Assert.Equal(TimeSpan.Zero, from.TimeOfDay);
    }

    [Fact]
    public void Window_IsMeasuredFromMidnight_EvenWhenGivenATimeOfDay()
    {
        var afternoon = Today.AddHours(15);

        Assert.Equal(Today, (AlertListFilter.None with { Window = AlertWindow.Today }).ToQuery(afternoon, false).From);
    }

    [Fact]
    public void Archive_SwapsTheStatusForResolved_AndKeepsTheRest()
    {
        var filter = new AlertListFilter(Pop, "Pop", AlertStatusChoice.NeedsResponse, AlertSeverityChoice.Urgent, AlertWindow.Today);

        var (severity, status, from) = filter.ToQuery(Today, archived: true);

        Assert.Equal("resolved", status);
        Assert.Equal("orange", severity);
        Assert.Equal(Today, from);
    }

    [Fact]
    public void Archive_DoesNotCountOrShowTheStatusPart()
    {
        var filter = AlertListFilter.None with { Status = AlertStatusChoice.Acknowledged, Severity = AlertSeverityChoice.Info };

        Assert.Equal(2, filter.ActiveCount(archived: false));
        Assert.Equal(1, filter.ActiveCount(archived: true));
        Assert.DoesNotContain(filter.Parts(archived: true), p => p.Part == AlertFilterPart.Status);
    }

    [Fact]
    public void Parts_AreInTheSheetsOrder_WithTheirWords()
    {
        var filter = new AlertListFilter(Pop, "Pop", AlertStatusChoice.NeedsResponse, AlertSeverityChoice.Critical, AlertWindow.Last30Days);

        Assert.Equal(
            ["Pop", "Needs response", "Critical", "Last 30 days"],
            filter.Parts(archived: false).Select(p => p.Label));
    }

    [Fact]
    public void AMemberWithNoName_StillShowsAPart_SoTheFilterCanBeUndone()
    {
        var filter = AlertListFilter.None with { MemberId = Pop };

        var part = Assert.Single(filter.Parts(archived: false));
        Assert.Equal(AlertFilterPart.Member, part.Part);
        Assert.Equal(AlertListFilter.UnnamedMemberLabel, part.Label);
    }

    [Theory]
    [InlineData(AlertFilterPart.Member)]
    [InlineData(AlertFilterPart.Status)]
    [InlineData(AlertFilterPart.Severity)]
    [InlineData(AlertFilterPart.Window)]
    public void Without_WidensOnlyThatPart(AlertFilterPart removed)
    {
        var filter = new AlertListFilter(Pop, "Pop", AlertStatusChoice.Acknowledged, AlertSeverityChoice.Notice, AlertWindow.Today);

        var widened = filter.Without(removed);

        var remaining = widened.Parts(archived: false).Select(p => p.Part).ToList();
        Assert.Equal(3, remaining.Count);
        Assert.DoesNotContain(removed, remaining);
        if (removed == AlertFilterPart.Member)
            Assert.Null(widened.MemberName);
    }
}
