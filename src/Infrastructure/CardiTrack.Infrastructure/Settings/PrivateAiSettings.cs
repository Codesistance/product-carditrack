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

    /// <summary>
    /// How long the Dashboard hero card's status line may spend generating before the request
    /// gives up and answers without one.
    /// </summary>
    /// <remarks>
    /// <see cref="TimeoutSeconds"/> is the right ceiling for a background job, which has all day;
    /// it is the wrong one for a request a caregiver is waiting on. The mobile client abandons the
    /// call at 30 s (<c>MauiProgram</c>), so anything above that is time nobody is left to receive
    /// — the phone has already given up and shown a socket error instead of the static per-tier
    /// copy the response contract provides for exactly this case.
    /// <para>
    /// 25 s leaves headroom for the surrounding queries and the round trip while still admitting a
    /// typical generation, which measures 21–26 s against CPU-served MedGemma. That overlap is
    /// uncomfortably tight, and lowering this is the knob that trades the live line for a snappier
    /// dashboard — but the real fix is making the model faster, not making the wait shorter.
    /// </para>
    /// </remarks>
    public int CurrentStatusBudgetSeconds { get; set; } = 25;
}
