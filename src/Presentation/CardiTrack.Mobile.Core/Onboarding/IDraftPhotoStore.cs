namespace CardiTrack.Mobile.Core.Onboarding;

/// <summary>
/// Somewhere durable to keep a picked photo while the draft lives. The photo picker hands
/// back a cache path that isn't guaranteed to survive a process restart, which is exactly
/// what a draft has to outlive.
/// </summary>
public interface IDraftPhotoStore
{
    bool Exists(string path);

    /// <summary>Copies the content to a new location and returns its path, or null on failure.</summary>
    Task<string?> SaveAsync(Stream content);

    void Delete(string path);
}
