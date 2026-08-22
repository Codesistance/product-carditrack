namespace CardiTrack.Application.Interfaces.Clients;

/// <summary>
/// A provider client that can be asked to load its model before anyone needs an answer from it.
/// Deliberately separate from <see cref="IExternalAiClient"/>: only a self-hosted model has a
/// load to pay for, and a hosted API has nothing to implement here.
/// </summary>
/// <remarks>
/// The cost this exists to hide is measured, not theoretical — the MedGemma service runs at
/// <c>min_instance_count = 0</c> and takes ~54 s to read its weights off disk on the first call
/// after an idle spell (docs/technical/medgemma_serving_architecture.md §9.1a). Batch passes do
/// not care; the caregiver who opens the app and asks a question does.
/// </remarks>
public interface IAiWarmUpClient
{
    /// <summary>
    /// Loads the model into memory, returning once it is resident. Generates nothing: the request
    /// carries no prompt, so no health data reaches the model and no tokens are billed or emitted.
    /// Throws what a normal call throws — the caller decides whether a failed warm-up matters.
    /// </summary>
    Task WarmUpAsync(CancellationToken ct = default);
}
