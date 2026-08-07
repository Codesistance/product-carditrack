using System.Diagnostics;
using System.Text.Json;

namespace CardiTrack.Mobile.Onboarding;

/// <summary>
/// The M1-04 form as last typed, saved locally so backgrounding the app — or the OS killing
/// it while the photo picker is open — doesn't cost the user their input. Nothing here exists
/// server-side until Continue is tapped, so this is the one part of the wizard that can't be
/// resumed from onboarding status.
///
/// The draft carries a name, date of birth and free-text medical notes, so it lives in
/// SecureStorage rather than Preferences; if the platform store is unavailable the draft is
/// dropped rather than written in the clear — a convenience feature degrading to nothing is
/// better than health data at rest in plaintext. Cleared on successful creation, on sign-out,
/// and once older than <see cref="Lifetime"/>. Skipping the step deliberately keeps it: "not
/// now" isn't "discard".
/// </summary>
internal sealed class CardiMemberDraft
{
    private const string StorageKey = "onboarding.cardimember_draft";
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    public string? Name { get; set; }
    public DateTime? DateOfBirth { get; set; }
    public int RelationshipIndex { get; set; } = -1;
    public bool DetailsExpanded { get; set; }
    public string? MedicalNotes { get; set; }
    public string? EmergencyContactName { get; set; }
    public string? EmergencyContactPhone { get; set; }
    public string? PhotoPath { get; set; }
    public DateTime SavedUtc { get; set; }

    /// <summary>Anything worth restoring? The details toggle alone doesn't count.</summary>
    public bool HasContent =>
        !string.IsNullOrWhiteSpace(Name)
        || DateOfBirth is not null
        || RelationshipIndex >= 0
        || !string.IsNullOrWhiteSpace(MedicalNotes)
        || !string.IsNullOrWhiteSpace(EmergencyContactName)
        || !string.IsNullOrWhiteSpace(EmergencyContactPhone)
        || !string.IsNullOrEmpty(PhotoPath);

    public static async Task<CardiMemberDraft?> LoadAsync()
    {
        try
        {
            var json = await SecureStorage.Default.GetAsync(StorageKey);
            if (string.IsNullOrEmpty(json))
                return null;

            var draft = JsonSerializer.Deserialize<CardiMemberDraft>(json);
            if (draft is null || DateTime.UtcNow - draft.SavedUtc > Lifetime)
            {
                await ClearAsync();
                return null;
            }

            // The photo is a file next to the payload; it may have been cleaned up under us.
            if (!string.IsNullOrEmpty(draft.PhotoPath) && !File.Exists(draft.PhotoPath))
                draft.PhotoPath = null;

            return draft.HasContent ? draft : null;
        }
        catch (Exception ex)
        {
            // Matches SecureTokenStore: the platform store throws a variety of native
            // exception types, and a draft we can't read is simply no draft.
            Debug.WriteLine($"CardiMember draft read failed; starting with an empty form. {ex.Message}");
            return null;
        }
    }

    public async Task SaveAsync()
    {
        if (!HasContent)
        {
            await ClearAsync();
            return;
        }

        SavedUtc = DateTime.UtcNow;
        try
        {
            await SecureStorage.Default.SetAsync(StorageKey, JsonSerializer.Serialize(this));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"CardiMember draft write failed; the form won't be restored. {ex.Message}");
        }
    }

    public static async Task ClearAsync()
    {
        try
        {
            var json = await SecureStorage.Default.GetAsync(StorageKey);
            if (!string.IsNullOrEmpty(json))
                DeletePhoto(JsonSerializer.Deserialize<CardiMemberDraft>(json)?.PhotoPath);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"CardiMember draft photo cleanup failed. {ex.Message}");
        }

        try
        {
            SecureStorage.Default.Remove(StorageKey);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"CardiMember draft clear failed. {ex.Message}");
        }
    }

    /// <summary>
    /// Copies a freshly picked photo into app data. The picker hands back a cache path that
    /// isn't guaranteed to survive a process restart, which is exactly what the draft has to
    /// outlive. Returns null if the copy fails; the caller can still show the original.
    /// </summary>
    public static async Task<string?> CapturePhotoAsync(FileResult photo, string? previousPath)
    {
        try
        {
            // A new name per pick: ImageSource.FromFile caches by path, so reusing one
            // filename would redisplay the previous photo.
            var target = Path.Combine(FileSystem.AppDataDirectory, $"cardimember-draft-{Guid.NewGuid():N}.img");
            await using (var source = await photo.OpenReadAsync())
            await using (var destination = File.Create(target))
            {
                await source.CopyToAsync(destination);
            }

            DeletePhoto(previousPath);
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"CardiMember draft photo copy failed. {ex.Message}");
            return null;
        }
    }

    private static void DeletePhoto(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return;

        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"CardiMember draft photo delete failed. {ex.Message}");
        }
    }
}
