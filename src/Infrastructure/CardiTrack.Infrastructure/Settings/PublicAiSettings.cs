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
    /// Required — every supported kind authenticates with one, and the clients pass it straight to
    /// the provider. Non-nullable rather than optional so the contract matches the validation.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Optional — each kind has a documented default endpoint. Set it to route through a gateway,
    /// a regional endpoint (e.g. Vertex AI), or a test double.
    /// </summary>
    public string? BaseUrl { get; set; }

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
