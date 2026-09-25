using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Security;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Domain.Entities;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Services;

/// <inheritdoc cref="IMedicalEntryService"/>
public class MedicalEntryService : IMedicalEntryService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICardiMemberAccessService _access;
    private readonly INotificationGapResolver _gapResolver;
    private readonly MedicalLedger _ledger;
    private readonly TimeProvider _timeProvider;

    public MedicalEntryService(
        IUnitOfWork unitOfWork,
        ICardiMemberAccessService access,
        IEncryptionService encryption,
        INotificationGapResolver gapResolver,
        TimeProvider? timeProvider = null)
    {
        _unitOfWork = unitOfWork;
        _access = access;
        _gapResolver = gapResolver;
        _ledger = new MedicalLedger(unitOfWork, encryption);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<MedicalEntriesResponse> GetAsync(
        Guid requestingUserId, Guid cardiMemberId, CancellationToken ct = default)
    {
        await _access.RequireViewAccessAsync(requestingUserId, cardiMemberId, ct);
        var member = await RequireActiveMemberAsync(cardiMemberId);

        var (entries, carriedOver) = await _ledger.LoadAsync(member, ct);

        // A read that carried the single note over has written a line; keep it, so the id the
        // screen is about to act on exists. The summary is already exactly that line, so it is
        // left alone — rewriting it here would re-date nothing and churn a nonce for no reason.
        if (carriedOver)
            await _unitOfWork.SaveChangesAsync();

        return await ToResponseAsync(entries);
    }

    public Task<MedicalEntriesResponse> AddAsync(
        Guid requestingUserId, Guid cardiMemberId, MedicalEntryKind kind, string text, CancellationToken ct = default) =>
        ChangeAsync(requestingUserId, cardiMemberId, ct, async (entries, now) =>
        {
            var added = _ledger.NewEntry(cardiMemberId, kind, text, requestingUserId, now);
            await _unitOfWork.MedicalEntries.AddAsync(added);
            entries.Add(added);
        });

    public Task<MedicalEntriesResponse> ReviseAsync(
        Guid requestingUserId, Guid cardiMemberId, Guid entryId, MedicalEntryKind kind, string text,
        CancellationToken ct = default) =>
        ChangeAsync(requestingUserId, cardiMemberId, ct, async (entries, now) =>
        {
            var old = RequireCurrent(entries, entryId);

            // Nothing changed: not a revision, and not worth a history line that says it was one.
            // Saving an unchanged line is still somebody reading it and standing by it, though.
            if (old.Kind == kind && string.Equals(_ledger.Reveal(old), text.Trim(), StringComparison.Ordinal))
            {
                Confirm(old, now);
                return;
            }

            var replacement = _ledger.NewEntry(cardiMemberId, kind, text, requestingUserId, now);
            await _unitOfWork.MedicalEntries.AddAsync(replacement);
            entries.Add(replacement);
            _ledger.Retire(old, requestingUserId, now, replacedBy: replacement.Id);
        });

    public Task<MedicalEntriesResponse> RemoveAsync(
        Guid requestingUserId, Guid cardiMemberId, Guid entryId, CancellationToken ct = default) =>
        ChangeAsync(requestingUserId, cardiMemberId, ct, (entries, now) =>
        {
            _ledger.Retire(RequireCurrent(entries, entryId), requestingUserId, now);
            return Task.CompletedTask;
        });

    public Task<MedicalEntriesResponse> ConfirmAsync(
        Guid requestingUserId, Guid cardiMemberId, Guid entryId, CancellationToken ct = default) =>
        ChangeAsync(requestingUserId, cardiMemberId, ct, (entries, now) =>
        {
            Confirm(RequireCurrent(entries, entryId), now);
            return Task.CompletedTask;
        });

    public Task<MedicalEntriesResponse> EraseAsync(
        Guid requestingUserId, Guid cardiMemberId, Guid entryId, CancellationToken ct = default) =>
        ChangeAsync(requestingUserId, cardiMemberId, ct, (entries, _) =>
        {
            var entry = entries.FirstOrDefault(e => e.Id == entryId)
                        ?? throw new KeyNotFoundException("That line isn't on file.");

            // A line that replaced this one still points back at nothing once it is gone, which is
            // fine — the pointer runs the other way (old → new). One that this line replaced would
            // keep pointing at a row that no longer exists; it is history either way, so it simply
            // reads as removed rather than changed.
            foreach (var predecessor in entries.Where(e => e.ReplacedByEntryId == entryId))
            {
                predecessor.ReplacedByEntryId = null;
                _unitOfWork.MedicalEntries.Update(predecessor);
            }

            _unitOfWork.MedicalEntries.Remove(entry);
            entries.Remove(entry);
            return Task.CompletedTask;
        });

    /// <summary>
    /// The shape of every write: manage access, the member's whole ledger loaded (carrying the
    /// single note over if need be), the change, the summary rewritten, one save — then the nudge
    /// gaps re-judged, so a card about missing or stale notes is gone by the time the screen repaints.
    /// </summary>
    private async Task<MedicalEntriesResponse> ChangeAsync(
        Guid requestingUserId, Guid cardiMemberId, CancellationToken ct,
        Func<List<MedicalEntry>, DateTime, Task> change)
    {
        await _access.RequireManageAccessAsync(requestingUserId, cardiMemberId, ct);
        var member = await RequireActiveMemberAsync(cardiMemberId);

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var (entries, _) = await _ledger.LoadAsync(member, ct);
        await change(entries, now);

        // Throws before the save when the list has outgrown what the summary can hold, so a
        // refused line leaves nothing half-written.
        _ledger.Summarise(member, entries, now);
        await _unitOfWork.SaveChangesAsync();

        await _gapResolver.ResolveForCardiMemberAsync(cardiMemberId, ct);

        return await ToResponseAsync(entries);
    }

    private void Confirm(MedicalEntry entry, DateTime utcNow)
    {
        entry.ConfirmedAtUtc = utcNow;
        entry.UpdatedDate = utcNow;
        _unitOfWork.MedicalEntries.Update(entry);
    }

    private static MedicalEntry RequireCurrent(List<MedicalEntry> entries, Guid entryId) =>
        entries.FirstOrDefault(e => e.Id == entryId && e.IsCurrent)
        ?? throw new KeyNotFoundException("That line isn't on the list any more.");

    private async Task<CardiMember> RequireActiveMemberAsync(Guid cardiMemberId)
    {
        var member = await _unitOfWork.CardiMembers.GetByIdAsync(cardiMemberId);
        if (member is null || !member.IsActive)
            throw new KeyNotFoundException("CardiMember not found");
        return member;
    }

    private async Task<MedicalEntriesResponse> ToResponseAsync(IReadOnlyCollection<MedicalEntry> entries)
    {
        // Names looked up once each: a ledger is a handful of lines written by one or two people.
        var names = new Dictionary<Guid, string?>();
        async Task<string?> NameOf(Guid? userId)
        {
            if (userId is not { } id)
                return null;
            if (!names.TryGetValue(id, out var name))
                names[id] = name = (await _unitOfWork.Users.GetByIdAsync(id))?.Name;
            return name;
        }

        async Task<MedicalEntryResponse> ToResponse(MedicalEntry e) => new()
        {
            Id = e.Id,
            Kind = e.Kind,
            Text = _ledger.Reveal(e),
            AddedAtUtc = e.AddedAtUtc,
            AddedByName = await NameOf(e.AddedByUserId),
            ConfirmedAtUtc = e.ConfirmedAtUtc,
            RemovedAtUtc = e.RemovedAtUtc,
            RemovedByName = await NameOf(e.RemovedByUserId),
            WasChanged = e.ReplacedByEntryId is not null,
        };

        var current = MedicalLedger.Ordered(entries.Where(e => e.IsCurrent)).ToList();
        var response = new MedicalEntriesResponse { ReviewedAtUtc = MedicalLedger.ReviewedAt(current) };
        foreach (var e in current)
            response.Current.Add(await ToResponse(e));
        foreach (var e in entries.Where(e => !e.IsCurrent).OrderByDescending(e => e.RemovedAtUtc))
            response.History.Add(await ToResponse(e));
        return response;
    }
}
