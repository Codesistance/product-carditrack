using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CardiTrack.Mobile.Core.Onboarding;

/// <summary>
/// Reads and writes the M1-04 form draft so backgrounding the app — or the OS killing it
/// while the photo picker is open — doesn't cost the user their typing.
///
/// The draft carries a name, date of birth and free-text medical notes, so it goes to
/// <see cref="ISecureKeyValueStore"/>; when that store is unavailable the draft is dropped
/// rather than written in the clear. A convenience feature degrading to nothing beats
/// health data at rest in plaintext.
///
/// Cleared on successful creation, on sign-out, and once older than <see cref="Lifetime"/>.
/// Skipping the step deliberately keeps the draft: "not now" isn't "discard".
/// </summary>
public sealed class CardiMemberDraftStore
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    private const string StorageKey = "onboarding.cardimember_draft";

    private readonly ISecureKeyValueStore _store;
    private readonly IDraftPhotoStore _photos;
    private readonly TimeProvider _clock;
    private readonly ILogger<CardiMemberDraftStore> _logger;

    public CardiMemberDraftStore(
        ISecureKeyValueStore store,
        IDraftPhotoStore photos,
        TimeProvider? clock = null,
        ILogger<CardiMemberDraftStore>? logger = null)
    {
        _store = store;
        _photos = photos;
        _clock = clock ?? TimeProvider.System;
        _logger = logger ?? NullLogger<CardiMemberDraftStore>.Instance;
    }

    public async Task<CardiMemberDraft?> LoadAsync()
    {
        var draft = await ReadAsync();
        if (draft is null)
            return null;

        if (_clock.GetUtcNow().UtcDateTime - draft.SavedUtc > Lifetime)
        {
            await ClearAsync();
            return null;
        }

        // The photo is a file alongside the payload; it may have been cleaned up under us.
        if (!string.IsNullOrEmpty(draft.PhotoPath) && !_photos.Exists(draft.PhotoPath))
            draft.PhotoPath = null;

        return draft.HasContent ? draft : null;
    }

    public async Task SaveAsync(CardiMemberDraft draft)
    {
        if (!draft.HasContent)
        {
            await ClearAsync();
            return;
        }

        draft.SavedUtc = _clock.GetUtcNow().UtcDateTime;
        try
        {
            await _store.SetAsync(StorageKey, JsonSerializer.Serialize(draft));
        }
        catch (Exception ex)
        {
            // The platform store throws a variety of native exception types; a draft we
            // can't write is a form that won't be restored, not a broken page.
            _logger.LogWarning(ex, "CardiMember draft write failed; the form won't be restored");
        }
    }

    public async Task ClearAsync()
    {
        var draft = await ReadAsync();
        if (!string.IsNullOrEmpty(draft?.PhotoPath))
            DeletePhoto(draft.PhotoPath);

        try
        {
            _store.Remove(StorageKey);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CardiMember draft clear failed");
        }
    }

    /// <summary>
    /// Copies a freshly picked photo somewhere durable and drops the one it replaces.
    /// Returns null if the copy fails, leaving the previous photo in place.
    /// </summary>
    public async Task<string?> CapturePhotoAsync(Stream content, string? previousPath)
    {
        string? path;
        try
        {
            path = await _photos.SaveAsync(content);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "CardiMember draft photo copy failed");
            return null;
        }

        if (path is null)
            return null;

        if (!string.IsNullOrEmpty(previousPath))
            DeletePhoto(previousPath);
        return path;
    }

    private async Task<CardiMemberDraft?> ReadAsync()
    {
        try
        {
            var json = await _store.GetAsync(StorageKey);
            return string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<CardiMemberDraft>(json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CardiMember draft read failed; starting with an empty form");
            return null;
        }
    }

    private void DeletePhoto(string path)
    {
        try
        {
            _photos.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "CardiMember draft photo delete failed");
        }
    }
}
