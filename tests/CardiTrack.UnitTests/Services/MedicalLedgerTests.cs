using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;

namespace CardiTrack.UnitTests.Services;

public class MedicalLedgerTests
{
    [Fact]
    public void Compose_LabelsEachLineByKind_ButLeavesOtherAsWritten()
    {
        var note = MedicalLedger.Compose(
        [
            (MedicalEntryKind.Allergy, "Penicillin"),
            (MedicalEntryKind.Other, "Walks with a stick"),
        ]);

        Assert.Equal("Allergy: Penicillin\nWalks with a stick", note);
    }

    /// <summary>
    /// What keeps an older build's echo from reading as an edit: a note carried over as one Other
    /// line summarises back to exactly the note, including its own line breaks.
    /// </summary>
    [Fact]
    public void Compose_OfASingleCarriedOverNote_IsTheNoteItself()
    {
        const string note = "Pacemaker fitted 2019\nAllergic to penicillin";

        Assert.Equal(note, MedicalLedger.Compose([(MedicalEntryKind.Other, note)]));
    }

    [Fact]
    public void Compose_OfNothing_IsNoNote()
    {
        Assert.Null(MedicalLedger.Compose([]));
    }

    [Fact]
    public void ReviewedAt_IsTheLeastRecentConfirmation()
    {
        var older = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var newer = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.Equal(older, MedicalLedger.ReviewedAt(
        [
            new MedicalEntry { ConfirmedAtUtc = newer },
            new MedicalEntry { ConfirmedAtUtc = older },
        ]));
    }

    [Fact]
    public void ReviewedAt_IsUnknown_WhenAnyLineWasNeverConfirmed_OrThereAreNone()
    {
        Assert.Null(MedicalLedger.ReviewedAt(
        [
            new MedicalEntry { ConfirmedAtUtc = DateTime.UtcNow },
            new MedicalEntry { ConfirmedAtUtc = null },
        ]));
        Assert.Null(MedicalLedger.ReviewedAt([]));
    }
}
