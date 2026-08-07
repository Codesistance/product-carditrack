namespace CardiTrack.Mobile.Core.Onboarding;

/// <summary>
/// Keeps draft photos as files in a directory the caller nominates — on device that's app
/// data, deliberately not the picker's cache directory, which isn't guaranteed to survive a
/// process restart.
/// </summary>
public sealed class FileDraftPhotoStore : IDraftPhotoStore
{
    private const string FilePrefix = "cardimember-draft-";

    private readonly string _directory;

    public FileDraftPhotoStore(string directory)
    {
        _directory = directory;
    }

    public bool Exists(string path) => File.Exists(path);

    public async Task<string?> SaveAsync(Stream content)
    {
        Directory.CreateDirectory(_directory);

        // A new name per pick: image sources cache by path, so reusing one filename would
        // redisplay the photo being replaced.
        var target = Path.Combine(_directory, $"{FilePrefix}{Guid.NewGuid():N}.img");
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
