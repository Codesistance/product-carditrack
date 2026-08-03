using CardiTrack.Observability.Providers;

namespace CardiTrack.Observability;

/// <summary>
/// Maps Apm:Engine values to providers. To support a new backend, implement
/// <see cref="IApmProvider"/> and add it here — nothing else changes in the apps.
/// </summary>
public static class ApmProviderRegistry
{
    private static readonly Dictionary<string, IApmProvider> Providers =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [BetterStackApmProvider.EngineName] = new BetterStackApmProvider(),
        };

    public static IReadOnlyCollection<string> KnownEngines => Providers.Keys;

    /// <summary>Misconfiguration is loud: an unknown engine fails startup, not silently ships nowhere.</summary>
    public static IApmProvider Resolve(string engine) =>
        Providers.TryGetValue(engine, out var provider)
            ? provider
            : throw new InvalidOperationException(
                $"Unknown APM engine '{engine}'. Known engines: {string.Join(", ", Providers.Keys)}. " +
                $"Set {ApmOptions.SectionName}:Engine to one of these, or leave it empty to disable shipping.");
}
