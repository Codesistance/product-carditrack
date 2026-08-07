using CardiTrack.Mobile.Core.Onboarding;

namespace CardiTrack.Mobile.Services;

/// <summary>
/// Keeps draft photos in app data, out of the picker's cache directory (which isn't
/// guaranteed to survive a process restart).
/// </summary>
public sealed class AppDataDraftPhotoStore : IDraftPhotoStore
{
    private const string FilePrefix = "cardimember-draft-";

    public bool Exists(string path) => File.Exists(path);

    public async Task<string?> SaveAsync(Stream content)
    {
        // A new name per pick: ImageSource.FromFile caches by path, so reusing one
        // filename would redisplay the previous photo.
        var target = Path.Combine(FileSystem.AppDataDirectory, $"{FilePrefix}{Guid.NewGuid():N}.img");
        await using (var destination = File.Create(target))
        {
            await content.CopyToAsync(destination);
        }
        return target;
    }

    public void Delete(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }
}
