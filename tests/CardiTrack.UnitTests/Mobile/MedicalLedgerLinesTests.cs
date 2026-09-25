using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Domain.Enums;
using CardiTrack.Mobile.Core.Members;

namespace CardiTrack.UnitTests.Mobile;

public class MedicalLedgerLinesTests
{
    private static readonly DateTime Added = new(2026, 9, 12, 10, 0, 0, DateTimeKind.Utc);

    private static MedicalEntryResponse Line(
        MedicalEntryKind kind = MedicalEntryKind.Allergy, string? by = "Jane", DateTime? confirmed = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            Text = "Penicillin",
            AddedAtUtc = Added,
            AddedByName = by,
            ConfirmedAtUtc = confirmed,
        };

    [Fact]
    public void Group_FollowsTheKindOrder_AndLeavesOutEmptyGroups()
    {
        var groups = MedicalLedgerLines.Group(
        [
            Line(MedicalEntryKind.Other),
            Line(MedicalEntryKind.Medication),
            Line(MedicalEntryKind.Condition),
            Line(MedicalEntryKind.Medication),
        ]);

        Assert.Equal(
            [MedicalEntryKind.Condition, MedicalEntryKind.Medication, MedicalEntryKind.Other],
            groups.Select(g => g.Kind));
        Assert.Equal(2, groups[1].Lines.Count);
    }

    [Fact]
    public void Caption_SaysWhoAddedIt_AndLeavesOutAConfirmationThatWasTheAdding()
    {
        Assert.Equal(
            "Added 12 Sep 2026 by Jane",
            MedicalLedgerLines.Caption(Line(confirmed: Added), TimeZoneInfo.Utc));
    }

    [Fact]
    public void Caption_AddsALaterConfirmation()
    {
        var caption = MedicalLedgerLines.Caption(Line(confirmed: Added.AddDays(30)), TimeZoneInfo.Utc);

        Assert.StartsWith("Added 12 Sep 2026 by Jane · confirmed ", caption);
    }

    /// <summary>A note carried over from before the ledger has no author and may never have been confirmed.</summary>
    [Fact]
    public void Caption_OfACarriedOverNote_ClaimsNoAuthor_AndSaysItIsUnconfirmed()
    {
        Assert.Equal(
            "On file since 12 Sep 2026 · not confirmed yet",
            MedicalLedgerLines.Caption(Line(by: null, confirmed: null), TimeZoneInfo.Utc));
    }

    [Fact]
    public void HistoryCaption_TellsAChangeFromARemoval()
    {
        var changed = Line();
        changed.RemovedAtUtc = Added.AddDays(3);
        changed.RemovedByName = "Tom";
        changed.WasChanged = true;
        var removed = Line();
        removed.RemovedAtUtc = Added.AddDays(3);

        Assert.Equal("Changed 15 Sep 2026 by Tom", MedicalLedgerLines.HistoryCaption(changed, TimeZoneInfo.Utc));
        Assert.Equal("Removed 15 Sep 2026", MedicalLedgerLines.HistoryCaption(removed, TimeZoneInfo.Utc));
    }

    [Fact]
    public void ReviewedLine_SaysPlainlyWhenSomethingWasNeverConfirmed()
    {
        Assert.StartsWith("Not every line is confirmed yet", MedicalLedgerLines.ReviewedLine(null));
        Assert.StartsWith(
            "Every line confirmed since 12 Sep 2026 · ",
            MedicalLedgerLines.ReviewedLine(Added, TimeZoneInfo.Utc));
    }
}
