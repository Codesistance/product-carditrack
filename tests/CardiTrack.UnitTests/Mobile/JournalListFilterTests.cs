using CardiTrack.Mobile.Core.Journal;

namespace CardiTrack.UnitTests.Mobile;

public class JournalListFilterTests
{
    private static readonly DateOnly Today = new(2026, 9, 26);

    [Fact]
    public void None_AsksForEveryEntry_AndNarrowsNothing()
    {
        var (urgency, from) = JournalListFilter.None.ToQuery(Today);

        Assert.Null(urgency);
        Assert.Null(from);
        Assert.False(JournalListFilter.None.IsNarrowed);
        Assert.Empty(JournalListFilter.None.Parts());
    }

    [Fact]
    public void BothParts_CombineIntoOneQuery()
    {
        var filter = new JournalListFilter(JournalUrgencyChoice.ActNow, JournalWindow.Last7Days);

        var (urgency, from) = filter.ToQuery(Today);

        Assert.Equal("act-now", urgency);
        Assert.Equal(new DateOnly(2026, 9, 20), from);
        Assert.Equal(2, filter.Parts().Count);
    }

    [Theory]
    [InlineData(JournalUrgencyChoice.Watch, "watch")]
    [InlineData(JournalUrgencyChoice.CheckIn, "check-in")]
    [InlineData(JournalUrgencyChoice.Concerning, "concerning")]
    [InlineData(JournalUrgencyChoice.ActNow, "act-now")]
    [InlineData(JournalUrgencyChoice.Any, null)]
    public void Urgency_UsesTheWireWord(JournalUrgencyChoice choice, string? wire) =>
        Assert.Equal(wire, (JournalListFilter.None with { Urgency = choice }).ToQuery(Today).Urgency);

    [Theory]
    [InlineData(JournalWindow.Last7Days, 2026, 9, 20)]
    [InlineData(JournalWindow.Last30Days, 2026, 8, 28)]
    [InlineData(JournalWindow.Last90Days, 2026, 6, 29)]
    public void Window_CountsWholeLocalDaysIncludingToday(JournalWindow window, int year, int month, int day) =>
        Assert.Equal(
            new DateOnly(year, month, day),
            (JournalListFilter.None with { Window = window }).ToQuery(Today).From);

    [Fact]
    public void Parts_AreInTheSheetsOrder_WithTheirWords()
    {
        var filter = new JournalListFilter(JournalUrgencyChoice.CheckIn, JournalWindow.Last90Days);

        Assert.Equal(["Check in", "Last 90 days"], filter.Parts().Select(p => p.Label));
    }

    [Theory]
    [InlineData(JournalFilterPart.Urgency)]
    [InlineData(JournalFilterPart.Window)]
    public void Without_WidensOnlyThatPart(JournalFilterPart removed)
    {
        var filter = new JournalListFilter(JournalUrgencyChoice.Concerning, JournalWindow.Last30Days);

        var remaining = filter.Without(removed).Parts().Select(p => p.Part).ToList();

        Assert.Equal(JournalFilterPart.Urgency == removed ? JournalFilterPart.Window : JournalFilterPart.Urgency, Assert.Single(remaining));
    }
}
