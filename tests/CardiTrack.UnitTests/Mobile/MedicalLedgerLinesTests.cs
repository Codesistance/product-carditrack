using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Domain.Enums;
using CardiTrack.Mobile.Core.Members;

namespace CardiTrack.UnitTests.Mobile;

public class MedicalLedgerLinesTests
{
    private static readonly DateTime Added = new(2026, 9, 12, 10, 0, 0, DateTimeKind.Utc);

    private static MedicalEntryResponse Line(
        MedicalEntryKind kind = MedicalEntryKind.Allergy, string? by = "Jane", DateTime? confirmed = null,
        string text = "Penicillin") =>
        new()
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            Text = text,
            AddedAtUtc = Added,
            AddedByName = by,
            ConfirmedAtUtc = confirmed,
        };

    /// <summary>What somebody arriving in a hurry needs first: allergies, then medicines.</summary>
    [Fact]
    public void Group_PutsAllergiesThenMedicationsFirst_AndLeavesOutEmptyGroups()
    {
        var groups = MedicalLedgerLines.Group(
        [
            Line(MedicalEntryKind.Other),
            Line(MedicalEntryKind.Condition),
            Line(MedicalEntryKind.Medication),
            Line(MedicalEntryKind.Medication),
            Line(MedicalEntryKind.Allergy),
        ]);

        Assert.Equal(
            [MedicalEntryKind.Allergy, MedicalEntryKind.Medication, MedicalEntryKind.Condition, MedicalEntryKind.Other],
            groups.Select(g => g.Kind));
        Assert.Equal(2, groups[1].Lines.Count);
    }

    [Fact]
    public void Caption_SaysWhoAndWhenOnce_AndLeavesOutAConfirmationThatWasTheAdding()
    {
        Assert.Equal(
            "Added by Jane · 12 Sep 2026",
            MedicalLedgerLines.Caption(Line(confirmed: Added), TimeZoneInfo.Utc));
    }

    [Fact]
    public void Caption_AddsALaterConfirmation()
    {
        var caption = MedicalLedgerLines.Caption(Line(confirmed: Added.AddDays(30)), TimeZoneInfo.Utc);

        Assert.StartsWith("Added by Jane · 12 Sep 2026 · confirmed ", caption);
    }

    /// <summary>No author: true both of a carried-over note and of a caregiver who has since left.</summary>
    [Fact]
    public void Caption_WithNoAuthor_ClaimsNone()
    {
        Assert.Equal(
            "On file since 12 Sep 2026",
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

        Assert.Equal("Changed by Tom · 15 Sep 2026", MedicalLedgerLines.HistoryCaption(changed, TimeZoneInfo.Utc));
        Assert.Equal("Removed · 15 Sep 2026", MedicalLedgerLines.HistoryCaption(removed, TimeZoneInfo.Utc));
    }

    [Fact]
    public void ReviewStatus_IsCurrentInsideSixMonths_DuePastThem_AndUnconfirmedWithoutADate()
    {
        var now = Added.AddDays(10);

        Assert.Equal(LedgerReviewTone.Current, MedicalLedgerLines.ReviewStatus(2, Added, now)!.Value.Tone);
        Assert.Equal(
            LedgerReviewTone.Due,
            MedicalLedgerLines.ReviewStatus(2, Added, Added + MedicalLedgerLines.ReviewInterval)!.Value.Tone);
        Assert.Equal(
            (LedgerReviewTone.Unconfirmed, "Not confirmed yet"),
            MedicalLedgerLines.ReviewStatus(2, null, now));
    }

    [Fact]
    public void ReviewStatus_OfAnEmptyList_IsNothing()
    {
        Assert.Null(MedicalLedgerLines.ReviewStatus(0, null, Added));
    }

    [Fact]
    public void SplitIntoStatements_CutsAtSentencesAndLines_AndDropsTheClosingStop()
    {
        Assert.Equal(
            ["Type 2 diabetes", "Metformin 500mg twice daily", "Penicillin allergy", "Walks with a stick"],
            MedicalLedgerLines.SplitIntoStatements(
                "Type 2 diabetes. Metformin 500mg twice daily. Penicillin allergy.\nWalks with a stick"));
    }

    /// <summary>A decimal point or an abbreviation is not the end of a statement.</summary>
    [Theory]
    [InlineData("Warfarin 0.5 mg daily")]
    [InlineData("Takes approx. 2 tablets a day")]
    public void SplitIntoStatements_KeepsDecimalsAndAbbreviationsWhole(string text)
    {
        Assert.Equal([text], MedicalLedgerLines.SplitIntoStatements(text));
    }

    [Fact]
    public void IsUnsortedBlock_OnlyForAnUnsignedOtherLineHoldingSeveralThings()
    {
        const string block = "Type 2 diabetes. Penicillin allergy.";

        Assert.True(MedicalLedgerLines.IsUnsortedBlock(Line(MedicalEntryKind.Other, by: null, text: block)));
        Assert.False(MedicalLedgerLines.IsUnsortedBlock(Line(MedicalEntryKind.Other, by: "Jane", text: block)));
        Assert.False(MedicalLedgerLines.IsUnsortedBlock(Line(MedicalEntryKind.Allergy, by: null, text: block)));
        Assert.False(MedicalLedgerLines.IsUnsortedBlock(Line(MedicalEntryKind.Other, by: null, text: "Type 2 diabetes.")));
    }
}
