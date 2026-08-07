using CardiTrack.Mobile.Core.Onboarding;

namespace CardiTrack.Mobile.Services;

/// <summary>
/// <see cref="ISecureKeyValueStore"/> over MAUI's platform secure storage (Keychain/Keystore).
/// Deliberately has no Preferences fallback: callers store health-adjacent data here, so an
/// unavailable secure store must mean "no value", not "write it in the clear".
/// </summary>
public sealed class SecureStorageKeyValueStore : ISecureKeyValueStore
{
    public Task<string?> GetAsync(string key) => SecureStorage.Default.GetAsync(key);

    public Task SetAsync(string key, string value) => SecureStorage.Default.SetAsync(key, value);

    public void Remove(string key) => SecureStorage.Default.Remove(key);
}
