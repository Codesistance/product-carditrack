using CardiTrack.Application.Exceptions;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Security;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;
using CardiTrack.UnitTests.Notifications;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace CardiTrack.UnitTests.Services;

public class MedicalEntryServiceTests
{
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ICardiMemberRepository _members = Substitute.For<ICardiMemberRepository>();
    private readonly IMedicalEntryRepository _entries = Substitute.For<IMedicalEntryRepository>();
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly ICardiMemberAccessService _access = Substitute.For<ICardiMemberAccessService>();
    private readonly IEncryptionService _encryption = Substitute.For<IEncryptionService>();
    private readonly NoOpNotificationGapResolver _gaps = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero));

    private readonly Guid _userId = Guid.NewGuid();
    private readonly List<MedicalEntry> _ledger = [];
    private readonly List<MedicalEntry> _staged = [];
    private readonly CardiMember _member;

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    public MedicalEntryServiceTests()
    {
        _unitOfWork.CardiMembers.Returns(_members);
        _unitOfWork.MedicalEntries.Returns(_entries);
        _unitOfWork.Users.Returns(_users);
        _members.LockForUpdateAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        _entries.GetByCardiMemberAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(_ => _ledger.ToList());
        // Added lines are staged until a save, and a cleared tracker drops them, the way EF does:
        // the carry-over depends on a discarded first attempt not being seen by the second.
        _entries.When(r => r.AddAsync(Arg.Any<MedicalEntry>()))
            .Do(c => _staged.Add(c.Arg<MedicalEntry>()));
        _unitOfWork.SaveChangesAsync().Returns(_ =>
        {
            _ledger.AddRange(_staged);
            _staged.Clear();
            return 1;
        });
        _unitOfWork.When(u => u.ClearTracking()).Do(_ => _staged.Clear());
        _entries.When(r => r.Remove(Arg.Any<MedicalEntry>()))
            .Do(c => _ledger.Remove(c.Arg<MedicalEntry>()));
        _users.GetByIdAsync(_userId).Returns(new User { Id = _userId, Name = "Jane" });

        // Reversible stand-in for AES, as CardiMemberServiceTests uses: legacy plaintext fails.
        _encryption.Encrypt(Arg.Any<string>()).Returns(c => $"enc({c.Arg<string>()})");
        _encryption.Decrypt(Arg.Any<string>()).Returns(c =>
        {
            var value = c.Arg<string>() ?? string.Empty;
            return value.StartsWith("enc(") && value.EndsWith(')')
                ? value[4..^1]
                : throw new FormatException("not ciphertext");
        });

        _member = new CardiMember { FirstName = "Margaret", LastName = "Doe", IsActive = true, CreatedDate = Now.AddYears(-1) };
        _members.GetByIdAsync(_member.Id).Returns(_member);
    }

    private MedicalEntryService CreateSut() => new(_unitOfWork, _access, _encryption, _gaps, _clock);

    private MedicalEntry Line(MedicalEntryKind kind, string text, int daysAgo = 30, bool confirmed = true)
    {
        var line = new MedicalEntry
        {
            CardiMemberId = _member.Id,
            Kind = kind,
            Text = $"enc({text})",
            AddedAtUtc = Now.AddDays(-daysAgo),
            AddedByUserId = _userId,
            ConfirmedAtUtc = confirmed ? Now.AddDays(-daysAgo) : null,
        };
        _ledger.Add(line);
        return line;
    }

    // ── carrying the single note over ──────────────────────────────────────────

    [Fact]
    public async Task Get_CarriesTheSingleNoteOver_AsOneOtherLine_DatedByItsReview()
    {
        var reviewed = Now.AddDays(-90);
        _member.MedicalNotes = "enc(Pacemaker fitted 2019)";
        _member.MedicalNotesReviewedAtUtc = reviewed;

        var result = await CreateSut().GetAsync(_userId, _member.Id);

        var line = Assert.Single(result.Current);
        Assert.Equal(MedicalEntryKind.Other, line.Kind);
        Assert.Equal("Pacemaker fitted 2019", line.Text);
        Assert.Equal(reviewed, line.AddedAtUtc);
        Assert.Equal(reviewed, line.ConfirmedAtUtc);
        Assert.Null(line.AddedByName);
        // Saved, so the id the screen acts on next exists; the note itself is left as it was.
        await _unitOfWork.Received(1).SaveChangesAsync();
        Assert.Equal("enc(Pacemaker fitted 2019)", _member.MedicalNotes);
    }

    [Fact]
    public async Task Get_CarriesALegacyPlaintextNoteOver_Encrypted()
    {
        _member.MedicalNotes = "Written before encryption";

        await CreateSut().GetAsync(_userId, _member.Id);

        Assert.Equal("enc(Written before encryption)", Assert.Single(_ledger).Text);
        // The note itself too: the carry-over is a write, and it must not leave the older
        // readers' copy of the same words in the clear.
        Assert.Equal("enc(Written before encryption)", _member.MedicalNotes);
    }

    /// <summary>
    /// The carry-over happens under the member's lock, so two first reads at once cannot each
    /// add a line: the second waits, and finds the first one's.
    /// </summary>
    [Fact]
    public async Task Get_CarriesOverUnderTheMembersLock()
    {
        _member.MedicalNotes = "enc(Pacemaker fitted 2019)";

        await CreateSut().GetAsync(_userId, _member.Id);

        await _unitOfWork.Received(1).BeginTransactionAsync();
        await _members.Received(1).LockForUpdateAsync(_member.Id, Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).CommitTransactionAsync();
    }

    [Fact]
    public async Task Get_WithNothingToCarryOver_TakesNoLock()
    {
        Line(MedicalEntryKind.Allergy, "Penicillin");

        await CreateSut().GetAsync(_userId, _member.Id);

        await _unitOfWork.DidNotReceive().BeginTransactionAsync();
    }

    [Fact]
    public async Task Get_WithNothingOnFile_SavesNothing()
    {
        var result = await CreateSut().GetAsync(_userId, _member.Id);

        Assert.Empty(result.Current);
        Assert.Empty(result.History);
        Assert.Null(result.ReviewedAtUtc);
        await _unitOfWork.DidNotReceive().SaveChangesAsync();
    }

    [Fact]
    public async Task Get_DoesNotCarryTheNoteOverTwice()
    {
        _member.MedicalNotes = "enc(Pacemaker fitted 2019)";
        Line(MedicalEntryKind.Other, "Pacemaker fitted 2019");

        await CreateSut().GetAsync(_userId, _member.Id);

        Assert.Single(_ledger);
        await _unitOfWork.DidNotReceive().SaveChangesAsync();
    }

    [Fact]
    public async Task Get_GroupsByKind_AndPutsTheMostRecentlyRemovedFirstInTheHistory()
    {
        Line(MedicalEntryKind.Other, "Walks with a stick", daysAgo: 50);
        Line(MedicalEntryKind.Medication, "Aspirin", daysAgo: 40);
        Line(MedicalEntryKind.Allergy, "Penicillin", daysAgo: 30);
        Line(MedicalEntryKind.Condition, "Type 2 diabetes", daysAgo: 20);
        var olderRemoval = Line(MedicalEntryKind.Medication, "Warfarin", daysAgo: 60);
        olderRemoval.RemovedAtUtc = Now.AddDays(-10);
        var newerRemoval = Line(MedicalEntryKind.Medication, "Statin", daysAgo: 60);
        newerRemoval.RemovedAtUtc = Now.AddDays(-2);

        var result = await CreateSut().GetAsync(_userId, _member.Id);

        Assert.Equal(
            ["Type 2 diabetes", "Penicillin", "Aspirin", "Walks with a stick"],
            result.Current.Select(e => e.Text));
        Assert.Equal(["Statin", "Warfarin"], result.History.Select(e => e.Text));
        Assert.Equal("Jane", result.Current[0].AddedByName);
    }

    [Fact]
    public async Task Get_NeedsOnlyViewAccess()
    {
        await CreateSut().GetAsync(_userId, _member.Id);

        await _access.Received(1).RequireViewAccessAsync(_userId, _member.Id, Arg.Any<CancellationToken>());
        await _access.DidNotReceive().RequireManageAccessAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    // ── adding ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Add_PutsALineOnFile_ByTheCaregiver_ConfirmedNow_AndRewritesTheNote()
    {
        Line(MedicalEntryKind.Other, "Walks with a stick", daysAgo: 10);

        var result = await CreateSut().AddAsync(_userId, _member.Id, MedicalEntryKind.Allergy, "  Penicillin ");

        var added = Assert.Single(result.Current, e => e.Kind == MedicalEntryKind.Allergy);
        Assert.Equal("Penicillin", added.Text);
        Assert.Equal("Jane", added.AddedByName);
        Assert.Equal(Now, added.ConfirmedAtUtc);
        Assert.Equal("enc(Allergy: Penicillin\nWalks with a stick)", _member.MedicalNotes);
        // The least recently confirmed line dates the whole list.
        Assert.Equal(Now.AddDays(-10), _member.MedicalNotesReviewedAtUtc);
        Assert.Equal(Now.AddDays(-10), result.ReviewedAtUtc);
        await _unitOfWork.Received(1).SaveChangesAsync();
        Assert.Contains(_member.Id, _gaps.ResolvedMembers);
    }

    [Fact]
    public async Task Add_ToAMemberWithANeverConfirmedLine_LeavesTheListUndated()
    {
        Line(MedicalEntryKind.Other, "Carried over", confirmed: false);

        await CreateSut().AddAsync(_userId, _member.Id, MedicalEntryKind.Allergy, "Penicillin");

        Assert.Null(_member.MedicalNotesReviewedAtUtc);
    }

    /// <summary>
    /// An older build echoes the note back on every profile save, and its validator caps the note
    /// at 2,000 characters. A list whose note grew past that would make that build's profile
    /// unsavable, so the line is refused before anything is written.
    /// </summary>
    [Fact]
    public async Task Add_RefusesALineThatWouldOutgrowTheNote_AndSavesNothing()
    {
        for (var i = 0; i < 4; i++)
            Line(MedicalEntryKind.Condition, new string('x', MedicalLedger.MaxEntryLength - 20));

        await Assert.ThrowsAsync<MedicalLedgerFullException>(
            () => CreateSut().AddAsync(_userId, _member.Id, MedicalEntryKind.Allergy, new string('y', 200)));

        await _unitOfWork.DidNotReceive().SaveChangesAsync();
        await _unitOfWork.Received(1).RollbackTransactionAsync();
    }

    [Fact]
    public async Task Add_NeedsManageAccess()
    {
        _access.RequireManageAccessAsync(_userId, _member.Id, Arg.Any<CancellationToken>())
            .ThrowsAsync(new KeyNotFoundException("CardiMember not found"));

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => CreateSut().AddAsync(_userId, _member.Id, MedicalEntryKind.Allergy, "Penicillin"));

        Assert.Empty(_ledger);
        await _unitOfWork.DidNotReceive().SaveChangesAsync();
    }

    // ── changing ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Revise_KeepsTheOldWordingInTheHistory_AsChanged()
    {
        var aspirin = Line(MedicalEntryKind.Medication, "Aspirin 75mg");

        var result = await CreateSut().ReviseAsync(
            _userId, _member.Id, aspirin.Id, MedicalEntryKind.Medication, "Aspirin 150mg");

        var current = Assert.Single(result.Current);
        Assert.Equal("Aspirin 150mg", current.Text);
        Assert.NotEqual(aspirin.Id, current.Id);
        var old = Assert.Single(result.History);
        Assert.Equal("Aspirin 75mg", old.Text);
        Assert.True(old.WasChanged);
        Assert.Equal("Jane", old.RemovedByName);
        Assert.Equal(Now, old.RemovedAtUtc);
        Assert.Equal("enc(Medication: Aspirin 150mg)", _member.MedicalNotes);
    }

    [Fact]
    public async Task Revise_WithTheSameWords_IsAConfirmation_NotAChange()
    {
        var aspirin = Line(MedicalEntryKind.Medication, "Aspirin", daysAgo: 200);

        var result = await CreateSut().ReviseAsync(
            _userId, _member.Id, aspirin.Id, MedicalEntryKind.Medication, "Aspirin ");

        Assert.Empty(result.History);
        Assert.Equal(aspirin.Id, Assert.Single(result.Current).Id);
        Assert.Equal(Now, aspirin.ConfirmedAtUtc);
    }

    [Fact]
    public async Task Revise_OfALineAlreadyInTheHistory_IsNotFound()
    {
        var old = Line(MedicalEntryKind.Medication, "Warfarin");
        old.RemovedAtUtc = Now.AddDays(-1);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => CreateSut().ReviseAsync(
            _userId, _member.Id, old.Id, MedicalEntryKind.Medication, "Warfarin 2mg"));
    }

    // ── removing, confirming, erasing ──────────────────────────────────────────

    [Fact]
    public async Task Remove_KeepsTheLineInTheHistory_AsRemoved()
    {
        var warfarin = Line(MedicalEntryKind.Medication, "Warfarin");

        var result = await CreateSut().RemoveAsync(_userId, _member.Id, warfarin.Id);

        Assert.Empty(result.Current);
        var removed = Assert.Single(result.History);
        Assert.False(removed.WasChanged);
        Assert.Equal(Now, removed.RemovedAtUtc);
        // Nothing current: the note and its date go with it, which is what the empty-notes nudge reads.
        Assert.Null(_member.MedicalNotes);
        Assert.Null(_member.MedicalNotesReviewedAtUtc);
    }

    [Fact]
    public async Task Remove_OfTheLastLine_IsNotUndoneByTheNextRead()
    {
        var warfarin = Line(MedicalEntryKind.Medication, "Warfarin");
        _member.MedicalNotes = "enc(Medication: Warfarin)";
        await CreateSut().RemoveAsync(_userId, _member.Id, warfarin.Id);

        var result = await CreateSut().GetAsync(_userId, _member.Id);

        Assert.Empty(result.Current);
    }

    [Fact]
    public async Task Confirm_DatesTheLine_AndTheListFromItsOldestConfirmation()
    {
        var allergy = Line(MedicalEntryKind.Allergy, "Penicillin", daysAgo: 400);
        Line(MedicalEntryKind.Medication, "Aspirin", daysAgo: 20);

        await CreateSut().ConfirmAsync(_userId, _member.Id, allergy.Id);

        Assert.Equal(Now, allergy.ConfirmedAtUtc);
        Assert.Equal(Now.AddDays(-20), _member.MedicalNotesReviewedAtUtc);
    }

    [Fact]
    public async Task Erase_DeletesTheRow_FromTheHistoryToo()
    {
        var warfarin = Line(MedicalEntryKind.Medication, "Warfarin");
        warfarin.RemovedAtUtc = Now.AddDays(-3);

        var result = await CreateSut().EraseAsync(_userId, _member.Id, warfarin.Id);

        _entries.Received(1).Remove(warfarin);
        Assert.Empty(result.History);
    }

    [Fact]
    public async Task Erase_OfAReplacement_LeavesWhatItReplacedReadingAsRemoved()
    {
        var old = Line(MedicalEntryKind.Medication, "Aspirin 75mg");
        var replacement = Line(MedicalEntryKind.Medication, "Aspirin 150mg");
        old.RemovedAtUtc = Now.AddDays(-1);
        old.ReplacedByEntryId = replacement.Id;

        var result = await CreateSut().EraseAsync(_userId, _member.Id, replacement.Id);

        Assert.Empty(result.Current);
        Assert.False(Assert.Single(result.History).WasChanged);
        Assert.Null(_member.MedicalNotes);
    }

    [Fact]
    public async Task Erase_OfSomeoneElsesLine_IsNotFound()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => CreateSut().EraseAsync(_userId, _member.Id, Guid.NewGuid()));

        await _unitOfWork.DidNotReceive().SaveChangesAsync();
    }

    /// <summary>
    /// Erasure takes the same member row first; a write that finds it gone writes nothing,
    /// rather than leaving lines behind for a member who no longer exists.
    /// </summary>
    [Fact]
    public async Task AnyChange_ToAMemberErasedMeanwhile_WritesNothing()
    {
        _members.LockForUpdateAsync(_member.Id, Arg.Any<CancellationToken>()).Returns(false);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => CreateSut().AddAsync(_userId, _member.Id, MedicalEntryKind.Allergy, "Penicillin"));

        Assert.Empty(_ledger);
        await _unitOfWork.DidNotReceive().SaveChangesAsync();
        await _unitOfWork.Received(1).RollbackTransactionAsync();
    }

    [Fact]
    public async Task AnyChange_ToARemovedMember_IsNotFound()
    {
        _member.IsActive = false;

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => CreateSut().AddAsync(_userId, _member.Id, MedicalEntryKind.Allergy, "Penicillin"));
    }
}
