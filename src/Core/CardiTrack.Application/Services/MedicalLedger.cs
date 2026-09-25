using System.Security.Cryptography;
using CardiTrack.Application.Exceptions;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Security;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Services;

/// <summary>
/// The rules shared by everything that writes a member's medical information: the ledger's own
/// endpoints (<see cref="MedicalEntryService"/>) and the older single-note paths on
/// <see cref="CardiMemberService"/> that app builds from before the ledger still call.
/// </summary>
/// <remarks>
/// <para>
/// The ledger is the record; <see cref="CardiMember.MedicalNotes"/> is its summary, rewritten by
/// <see cref="Summarise"/> after every change. Every existing reader of the single note — the AI
/// prompt, the two nudge rules, the edit form of an older build — sees the current lines as one
/// note and needs no change.
/// </para>
/// <para>
/// Notes written before the ledger are carried over the first time anything touches the member's
/// ledger (<see cref="LoadAsync"/>), not by a migration: they are encrypted, and only the
/// application holds the key. Until then the single note is still the summary of a ledger that
/// would hold exactly it, so nothing reads anything different in the meantime.
/// </para>
/// </remarks>
public sealed class MedicalLedger
{
    /// <summary>The longest one line may be. A line is one condition or one medication, not a history.</summary>
    public const int MaxEntryLength = 500;

    /// <summary>
    /// The longest the summary may grow — the single-note cap the create and edit validators
    /// enforce. Held here too because an older build echoes the summary back on every profile save,
    /// and a summary past its own validator's limit would make that build's profile unsavable.
    /// </summary>
    public const int MaxSummaryLength = 2000;

    private readonly IUnitOfWork _unitOfWork;
    private readonly IEncryptionService _encryption;

    public MedicalLedger(IUnitOfWork unitOfWork, IEncryptionService encryption)
    {
        _unitOfWork = unitOfWork;
        _encryption = encryption;
    }

    /// <summary>
    /// Runs <paramref name="work"/> in a transaction that holds the member's row lock
    /// (<see cref="ICardiMemberRepository.LockForUpdateAsync"/>), taken before anything is read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every write here reads the member and the ledger and then writes back a summary derived from
    /// both, so two at once would each save a summary missing the other's line, and two first reads
    /// would each carry the single note over. With the lock the second waits, then reads what the
    /// first committed. <paramref name="work"/> must load the member itself, after the lock — a copy
    /// loaded before it is the stale one EF keeps.
    /// </para>
    /// <para>
    /// The same lock stands between these writes and member erasure, which takes <c>FOR UPDATE</c>
    /// on the row first: a write either finds the member already gone (<see cref="KeyNotFoundException"/>,
    /// nothing written) or finishes before erasure sweeps the rows it wrote.
    /// </para>
    /// </remarks>
    public async Task<T> SerializedAsync<T>(Guid cardiMemberId, Func<Task<T>> work, CancellationToken ct = default)
    {
        await _unitOfWork.BeginTransactionAsync();
        try
        {
            if (!await _unitOfWork.CardiMembers.LockForUpdateAsync(cardiMemberId, ct))
                throw new KeyNotFoundException("CardiMember not found");

            var result = await work();
            await _unitOfWork.CommitTransactionAsync();
            return result;
        }
        catch
        {
            await _unitOfWork.RollbackTransactionAsync();
            // The refused change must not ride along on this unit of work's next save.
            _unitOfWork.ClearTracking();
            throw;
        }
    }

    /// <summary>
    /// Every line on file for the member, current and past — carrying the single note over as the
    /// first line if it has not been yet.
    /// </summary>
    /// <returns>
    /// The lines, and whether one was just carried over — added to the unit of work, not yet saved.
    /// </returns>
    public async Task<(List<MedicalEntry> Entries, bool CarriedOver)> LoadAsync(
        CardiMember member, CancellationToken ct = default)
    {
        var entries = await _unitOfWork.MedicalEntries.GetByCardiMemberAsync(member.Id, ct);
        var revealedNotes = RevealNotes(member.MedicalNotes);

        // Only when there is a note and nothing current to account for it. A member whose lines
        // were all removed has an empty note (Summarise cleared it), so this never resurrects one.
        if (!string.IsNullOrWhiteSpace(revealedNotes) && !entries.Any(e => e.IsCurrent))
        {
            var carried = new MedicalEntry
            {
                CardiMemberId = member.Id,
                Kind = MedicalEntryKind.Other,
                // As typed, untrimmed: the summary of this one line must be the note itself.
                Text = _encryption.Encrypt(revealedNotes),
                // The note recorded neither author nor date written. Its review date is the best
                // "on file since" there is; failing that, the member's own.
                AddedAtUtc = member.MedicalNotesReviewedAtUtc ?? member.CreatedDate,
                AddedByUserId = null,
                ConfirmedAtUtc = member.MedicalNotesReviewedAtUtc,
            };
            await _unitOfWork.MedicalEntries.AddAsync(carried);
            entries.Add(carried);
            return (entries, true);
        }

        return (entries, false);
    }

    /// <summary>A new current line, encrypted for storage.</summary>
    /// <remarks>
    /// The <see cref="MaxEntryLength"/> cap is the ledger endpoints' rule, enforced by their
    /// validator, and deliberately not here. The single-note paths an older build still calls
    /// (create, and a whole-note edit) take up to <see cref="MaxSummaryLength"/> and store it as one
    /// Other line: refusing those would break that build's form, and cutting them short would lose
    /// what somebody wrote. Such a line reads and confirms like any other; changing it through the
    /// ledger asks for it in lines of the ordinary length.
    /// </remarks>
    public MedicalEntry NewEntry(Guid cardiMemberId, MedicalEntryKind kind, string text, Guid? byUserId, DateTime utcNow) =>
        new()
        {
            CardiMemberId = cardiMemberId,
            Kind = kind,
            Text = _encryption.Encrypt(text.Trim()),
            AddedAtUtc = utcNow,
            AddedByUserId = byUserId,
            // Written just now, so current by construction — the same stance the single note took.
            ConfirmedAtUtc = utcNow,
        };

    /// <summary>Takes a line off the current list, keeping it in the history.</summary>
    public void Retire(MedicalEntry entry, Guid? byUserId, DateTime utcNow, Guid? replacedBy = null)
    {
        entry.RemovedAtUtc = utcNow;
        entry.RemovedByUserId = byUserId;
        entry.ReplacedByEntryId = replacedBy;
        entry.UpdatedDate = utcNow;
        _unitOfWork.MedicalEntries.Update(entry);
    }

    /// <summary>
    /// The member's single note in the clear. Notes written before they were encrypted are still in
    /// the database as plain text, and AES-GCM's authentication tag makes those indistinguishable
    /// from corruption — so rather than fail the whole screen, a value that will not decrypt is
    /// returned as stored: a legacy row reads back as what was typed, and every write re-stores it
    /// encrypted.
    /// </summary>
    public string? RevealNotes(string? storedNotes)
    {
        if (string.IsNullOrEmpty(storedNotes))
            return null;

        try
        {
            return _encryption.Decrypt(storedNotes);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or CryptographicException)
        {
            return storedNotes;
        }
    }

    /// <summary>
    /// Whether the stored note is plain text rather than ciphertext — the case
    /// <see cref="RevealNotes"/> silently tolerates. Asked by testing the same thing it does, so the
    /// two cannot disagree about what a legacy row is.
    /// </summary>
    public bool IsLegacyPlaintext(string? storedNotes)
    {
        if (string.IsNullOrEmpty(storedNotes))
            return false;

        try
        {
            _encryption.Decrypt(storedNotes);
            return false;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or CryptographicException)
        {
            return true;
        }
    }

    /// <summary>What a line says, in the clear. No plaintext fallback: these were never stored any other way.</summary>
    public string Reveal(MedicalEntry entry) => _encryption.Decrypt(entry.Text);

    /// <summary>
    /// Rewrites the member's single note and its review date from the current lines — the step
    /// every write ends with, so the summary never disagrees with the ledger.
    /// </summary>
    /// <exception cref="MedicalLedgerFullException">
    /// The summary would outgrow <see cref="MaxSummaryLength"/>. Thrown before anything is saved.
    /// </exception>
    public void Summarise(CardiMember member, IEnumerable<MedicalEntry> entries, DateTime utcNow)
    {
        var current = Ordered(entries.Where(e => e.IsCurrent)).ToList();
        var summary = Compose(current.Select(e => (e.Kind, Reveal(e))));

        if (summary is { Length: > MaxSummaryLength })
            throw new MedicalLedgerFullException(
                "That's more than the medical information can hold. Shorten or remove a line first.");

        // Rewritten only when the words changed or the row is still legacy plain text: encryption
        // takes a fresh nonce every call, so re-storing sound ciphertext of the same note would
        // churn the column on every confirmation for nothing.
        if (!string.Equals(RevealNotes(member.MedicalNotes), summary, StringComparison.Ordinal)
            || IsLegacyPlaintext(member.MedicalNotes))
        {
            member.MedicalNotes = summary is null ? null : _encryption.Encrypt(summary);
        }

        member.MedicalNotesReviewedAtUtc = ReviewedAt(current);
        member.UpdatedDate = utcNow;
        _unitOfWork.CardiMembers.Update(member);
    }

    /// <summary>Kind first, in the order a caregiver scans for them, then the order they went on file.</summary>
    public static IEnumerable<MedicalEntry> Ordered(IEnumerable<MedicalEntry> entries) =>
        entries
            .OrderBy(e => e.Kind)
            .ThenBy(e => e.AddedAtUtc)
            .ThenBy(e => e.CreatedDate);

    /// <summary>
    /// The current lines as one note: a line per entry, labelled by kind — except
    /// <see cref="MedicalEntryKind.Other"/>, which reads as it was written. That exception is what
    /// makes a note carried over from before the ledger read back exactly as it was typed, so an
    /// older build echoing it back is not mistaken for an edit. Null when there are no lines.
    /// </summary>
    public static string? Compose(IEnumerable<(MedicalEntryKind Kind, string Text)> lines)
    {
        var rendered = lines
            .Select(l => l.Kind == MedicalEntryKind.Other ? l.Text : $"{l.Kind}: {l.Text}")
            .ToList();
        return rendered.Count == 0 ? null : string.Join('\n', rendered);
    }

    /// <summary>
    /// When the list as a whole was last known to hold: its least recently confirmed line. Null
    /// when any line has never been confirmed — the list then contains something of unknown age,
    /// which is what the staleness rule's "never confirmed" wording is for — and when it is empty.
    /// </summary>
    public static DateTime? ReviewedAt(IReadOnlyCollection<MedicalEntry> current) =>
        current.Count == 0 || current.Any(e => e.ConfirmedAtUtc is null)
            ? null
            : current.Min(e => e.ConfirmedAtUtc);
}
