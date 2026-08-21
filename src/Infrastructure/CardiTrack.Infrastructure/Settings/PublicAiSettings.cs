namespace CardiTrack.Infrastructure.Settings;

/// <summary>
/// The swappable, off-estate provider used for reports and chat (AI:Public).
/// </summary>
/// <remarks>
/// Prompts sent here leave the project, so nothing that identifies a member goes with them —
/// see <see cref="Services.ReportGenerationService"/> and the chat controller, which
/// pseudonymise before calling. Medical analysis never uses this provider; it is pinned to the
/// in-VPC MedGemma service described by <see cref="PrivateAiSettings"/>.
/// </remarks>
public class PublicAiSettings
{
    public PublicAiProviderKind Kind { get; set; }

    public string Model { get; set; } = string.Empty;

    /// <summary>
    /// Required for the Gemini and Anthropic kinds, which authenticate with one; the VertexGemini
    /// kind authenticates by IAM (ADC access token) and ignores it. Non-nullable so the contract
    /// matches the per-kind validation.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Optional — each kind has a documented default endpoint (for VertexGemini it derives from
    /// <see cref="Location"/>). Set it to route through a gateway or a test double.
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>VertexGemini kind: GCP project the calls run in. Required for that kind.</summary>
    public string? ProjectId { get; set; }

    /// <summary>VertexGemini kind: regional location (e.g. <c>europe-west2</c>). Required for that
    /// kind, and must stay an EU region — the DPIA excludes US processing and the global endpoint.</summary>
    public string? Location { get; set; }

    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Upper bound on a single completion. Anthropic requires this on every request; Gemini
    /// applies its own model default when unset, so the value only binds where the provider reads it.
    /// </summary>
    /// <remarks>
    /// The default is deliberately generous rather than minimal: a multi-member report is the
    /// longest thing we ask a public model for, and a low ceiling truncates it mid-sentence with no
    /// error to catch. Non-streaming requests much above this risk client-side HTTP timeouts.
    /// </remarks>
    public int MaxOutputTokens { get; set; } = 16000;
}
