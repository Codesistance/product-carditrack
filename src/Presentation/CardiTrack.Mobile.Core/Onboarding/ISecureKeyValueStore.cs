namespace CardiTrack.Mobile.Core.Onboarding;

/// <summary>
/// Platform-encrypted key/value storage (Keychain/Keystore). Implementations may throw —
/// callers are expected to treat a failure as "no value", never as a reason to fall back
/// to storage that isn't encrypted.
/// </summary>
public interface ISecureKeyValueStore
{
    Task<string?> GetAsync(string key);
    Task SetAsync(string key, string value);
    void Remove(string key);
}
