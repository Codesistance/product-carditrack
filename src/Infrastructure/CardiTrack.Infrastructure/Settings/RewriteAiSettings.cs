namespace CardiTrack.Infrastructure.Settings;

/// <summary>
/// The non-medical rewrite provider (AI:Rewrite) — a plain instruction-tuned model used only on
/// member chat's non-clinical steps: rewriting MedGemma's clinical read into caregiver-facing
/// language, the malicious-check, the query plan and the waiting sentences. Selected by
/// <see cref="Kind"/>: self-hosted Ollama locally, Gemini via Vertex AI in deployed environments
/// (see <see cref="RewriteAiProviderKind"/> for the compliance posture).
/// </summary>
/// <remarks>
/// The clinical slot (<see cref="PrivateAiSettings"/>) deliberately has no kind switch — this one
/// does because its prompts are the narrowest on the platform: no name, no id, no MedicalNotes,
/// no questionnaire answers ever reach it (see <c>MemberChatService</c>).
/// </remarks>
public class RewriteAiSettings : IMedGemmaModelSettings
{
    /// <summary>Defaults to <see cref="RewriteAiProviderKind.Ollama"/> so local dev and
    /// docker-compose need no configuration and no GCP credentials.</summary>
    public RewriteAiProviderKind Kind { get; set; }

    public string Model { get; set; } = string.Empty;

    /// <summary>
    /// Required for the Ollama kind: the self-hosted host serving the rewrite model tag. Unused by
    /// the VertexGemini kind (see <see cref="VertexBaseUrl"/>), but tolerated when present so a
    /// deployment can carry both during a provider transition.
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// The Vertex kind answers in seconds, not the minutes a CPU-served Ollama cold start needs —
    /// deployments set this per kind rather than sharing one generous ceiling.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 300;

    /// <summary>Ollama kind only — same IAM-authorisation requirement as
    /// <see cref="PrivateAiSettings.UseIdentityToken"/>. The VertexGemini kind always
    /// authenticates (ADC access token) and ignores this flag.</summary>
    public bool UseIdentityToken { get; set; }

    /// <summary>VertexGemini kind: GCP project the calls run in. Required for that kind.</summary>
    public string? ProjectId { get; set; }

    /// <summary>VertexGemini kind: regional location (e.g. <c>europe-west2</c>). Required for that
    /// kind, and must stay an EU region — the DPIA excludes US processing and the global endpoint.</summary>
    public string? Location { get; set; }

    /// <summary>
    /// Upper bound on a single completion, on both kinds — Vertex's <c>maxOutputTokens</c> and
    /// Ollama's <c>num_predict</c> mean the same thing. Rewrite-slot replies are short (a chat
    /// reply, a query plan, three waiting sentences), so the default is far below
    /// <see cref="PublicAiSettings.MaxOutputTokens"/>.
    /// </summary>
    public int MaxOutputTokens { get; set; } = 8192;

    /// <summary>
    /// <inheritdoc cref="IMedGemmaModelSettings.ContextTokens" path="/summary"/>
    /// Ollama kind only — the Vertex kind's window is a property of the model, not a request
    /// parameter.
    /// </summary>
    /// <remarks>
    /// Larger than <see cref="MaxOutputTokens"/> by more than a rewrite prompt is ever likely to
    /// be: the two are not alternatives, and a window that only just fits the output ceiling
    /// leaves nothing for the prompt. Startup refuses a pair that gets this backwards
    /// (<c>AiServiceExtensions.RequireOutputFitsContext</c>), which is why the default is the
    /// output ceiling plus a prompt's worth rather than a round number that happens to match it.
    /// </remarks>
    public int ContextTokens { get; set; } = 12288;

    /// <summary>
    /// <inheritdoc cref="IMedGemmaModelSettings.LogClinicalOutput" path="/summary"/>
    /// </summary>
    /// <remarks>
    /// Only read when <see cref="Kind"/> is Ollama, since only then does a
    /// <c>MedGemmaClient</c> serve this slot. Same production refusal as the Private slot.
    /// </remarks>
    public bool LogClinicalOutput { get; set; }

    /// <summary>
    /// VertexGemini kind: endpoint override for test doubles. Deliberately a separate key from
    /// <see cref="BaseUrl"/> — during a provider transition the deployed environment still mounts
    /// the old Ollama URL under BaseUrl, and it must never be mistaken for a Vertex endpoint.
    /// When unset the endpoint derives from <see cref="Location"/>. Restricted at startup to
    /// allowed-region Vertex hosts or loopback: every Vertex request carries a live ADC bearer
    /// token, so an arbitrary override would be a credential-exfiltration vector.
    /// </summary>
    public string? VertexBaseUrl { get; set; }
}
