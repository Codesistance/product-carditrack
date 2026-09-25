using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Domain.Enums;

namespace CardiTrack.Application.Interfaces.Services;

/// <summary>
/// A member's medical information as a ledger of lines — see <see cref="Domain.Entities.MedicalEntry"/>.
/// </summary>
/// <remarks>
/// Reading needs view access; every change needs manage access, the same split the single note had
/// (a relative invited to watch cannot rewrite somebody's allergies). Denial and an unknown member
/// or line are both <see cref="KeyNotFoundException"/>, for the non-disclosure reason
/// <see cref="ICardiMemberAccessService"/> gives. Every call returns the whole ledger as it now
/// stands, so a screen can redraw from the answer.
/// </remarks>
public interface IMedicalEntryService
{
    Task<MedicalEntriesResponse> GetAsync(Guid requestingUserId, Guid cardiMemberId, CancellationToken ct = default);

    /// <exception cref="InvalidOperationException">The list would outgrow what it can hold.</exception>
    Task<MedicalEntriesResponse> AddAsync(
        Guid requestingUserId, Guid cardiMemberId, MedicalEntryKind kind, string text, CancellationToken ct = default);

    /// <summary>
    /// Changes a current line. The old wording is kept in the history, marked as changed, and a new
    /// line takes its place — dated now and confirmed by whoever made the change.
    /// </summary>
    /// <exception cref="InvalidOperationException">The list would outgrow what it can hold.</exception>
    Task<MedicalEntriesResponse> ReviseAsync(
        Guid requestingUserId, Guid cardiMemberId, Guid entryId, MedicalEntryKind kind, string text,
        CancellationToken ct = default);

    /// <summary>Takes a current line off the list. It stays in the history.</summary>
    Task<MedicalEntriesResponse> RemoveAsync(
        Guid requestingUserId, Guid cardiMemberId, Guid entryId, CancellationToken ct = default);

    /// <summary>Records that a current line still holds, without changing it.</summary>
    Task<MedicalEntriesResponse> ConfirmAsync(
        Guid requestingUserId, Guid cardiMemberId, Guid entryId, CancellationToken ct = default);

    /// <summary>
    /// Deletes a line outright, from the list or from the history. Not an archive: the row is gone
    /// — see the erasure note on <see cref="Domain.Entities.MedicalEntry"/>.
    /// </summary>
    Task<MedicalEntriesResponse> EraseAsync(
        Guid requestingUserId, Guid cardiMemberId, Guid entryId, CancellationToken ct = default);
}
