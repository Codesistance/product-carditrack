using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;

namespace CardiTrack.Application.Interfaces.Services;

/// <summary>
/// Reads and changes when a CardiMember's CardiJournal books are written.
/// </summary>
public interface IJournalSettingsService
{
    /// <summary>
    /// This member's timings, chosen and effective. Requires view access — knowing when a book
    /// lands is not health data, but the member's existence is.
    /// </summary>
    Task<JournalSettingsResponse> GetAsync(
        Guid requestingUserId, Guid cardiMemberId, CancellationToken ct = default);

    /// <summary>
    /// Changes the timings. Requires <em>manage</em> access, not view: moving when a book is
    /// written changes what every other caregiver receives, so it belongs with the primary
    /// caregiver alongside pausing monitoring.
    /// </summary>
    /// <exception cref="ArgumentException">A time is outside the selectable window or off the half-hour step.</exception>
    Task<JournalSettingsResponse> UpdateAsync(
        Guid requestingUserId,
        Guid cardiMemberId,
        UpdateJournalSettingsRequest request,
        CancellationToken ct = default);
}
