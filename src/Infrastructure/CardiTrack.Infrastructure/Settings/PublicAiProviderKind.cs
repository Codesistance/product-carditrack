namespace CardiTrack.Infrastructure.Settings;

/// <summary>
/// Wire protocols the public (off-estate) AI provider can speak. The value selects which
/// <see cref="Application.Interfaces.Clients.IExternalAiClient"/> implementation is built —
/// swapping provider is a configuration change, not a code change.
/// </summary>
/// <remarks>
/// Deliberately a closed enum rather than free text: every kind needs a client that speaks
/// that provider's wire format, so an unrecognised value is a deployment error we want to
/// fail on at startup rather than a provider we could plausibly reach.
/// Adding one is a new client class plus a member here — nothing else changes.
/// </remarks>
public enum PublicAiProviderKind
{
    Gemini,
    Anthropic
}
