namespace CardiTrack.Infrastructure.Settings;

/// <summary>
/// The self-hosted medical provider used for health insights (AI:Private) — MedGemma served by
/// Ollama on the internal-only Cloud Run service.
/// </summary>
/// <remarks>
/// There is no provider kind here, and that is the point: the medical path is pinned to MedGemma
/// in code, so no configuration change can route health data — including free-text MedicalNotes —
/// to an off-estate model. Only where MedGemma lives and which weights it serves are configurable.
/// </remarks>
public class PrivateAiSettings
{
    public string Model { get; set; } = string.Empty;

    /// <summary>
    /// Required. In dev/prod this is the MedGemma service URL, written to Secret Manager by CI
    /// after each deployment; locally it is the Ollama endpoint.
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Generous by default — CPU inference on a 4B model is measured in tens of seconds.</summary>
    public int TimeoutSeconds { get; set; } = 300;
}
