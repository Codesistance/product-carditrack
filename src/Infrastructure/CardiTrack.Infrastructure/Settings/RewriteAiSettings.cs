namespace CardiTrack.Infrastructure.Settings;

/// <summary>
/// The self-hosted, non-medical rewrite provider (AI:Rewrite) — a plain instruction-tuned model
/// served by the same Ollama instance as MedGemma, used only to rewrite MedGemma's clinical
/// output into caregiver-facing language. See <see cref="PrivateAiSettings"/> for the sibling
/// slot this mirrors.
/// </summary>
/// <remarks>
/// There is no provider kind here either, for the same reason as <see cref="PrivateAiSettings"/>:
/// this slot exists specifically so a rewrite pass never has to leave the project to run. Only
/// where the model lives and which weights it serves are configurable.
/// </remarks>
public class RewriteAiSettings : IMedGemmaModelSettings
{
    public string Model { get; set; } = string.Empty;

    /// <summary>
    /// Required. Points at the same host as <see cref="PrivateAiSettings.BaseUrl"/> in every
    /// deployed environment today — one Ollama instance serves both models, selected per call by
    /// <see cref="Model"/> — but is its own config key rather than inherited, so this slot
    /// validates independently of Private.
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Rewriting is a lighter task than clinical interpretation, but still CPU inference
    /// on the same shared instance — kept generous for the same reason as
    /// <see cref="PrivateAiSettings.TimeoutSeconds"/>.</summary>
    public int TimeoutSeconds { get; set; } = 300;

    /// <summary>Same IAM-authorisation requirement as <see cref="PrivateAiSettings.UseIdentityToken"/>
    /// — the Cloud Run host authorises by <c>roles/run.invoker</c> regardless of which model on it
    /// is being called.</summary>
    public bool UseIdentityToken { get; set; }
}
