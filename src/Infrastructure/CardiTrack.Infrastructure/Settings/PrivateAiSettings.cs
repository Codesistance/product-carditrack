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
public class PrivateAiSettings : IMedGemmaModelSettings
{
    public string Model { get; set; } = string.Empty;

    /// <summary>
    /// Required. In dev/prod this is the MedGemma service URL, written to Secret Manager by CI
    /// after each deployment; locally it is the Ollama endpoint.
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Generous by default — CPU inference on a 4B model is measured in tens of seconds.</summary>
    public int TimeoutSeconds { get; set; } = 300;

    // CurrentStatusBudgetSeconds was removed with the batch move: the status line is generated
    // by the pipeline (StatusLineGenerationService) and served from its persisted row, so no
    // request waits on a generation and the budget has nothing left to protect.

    /// <summary>
    /// Whether to attach a Google-minted OIDC identity token to every MedGemma request, with the
    /// audience set to <see cref="BaseUrl"/>. Required in dev/prod: the Cloud Run service authorises
    /// callers by IAM (<c>roles/run.invoker</c>) rather than by network position, and rejects an
    /// unauthenticated request at the Google front end before it reaches Ollama.
    /// </summary>
    /// <remarks>
    /// Defaults to <c>false</c> so a local Ollama over plain HTTP — docker-compose, tests — needs no
    /// credential and receives no bearer token. That default fails <em>closed</em> if an environment
    /// forgets to set it: the call 403s rather than silently downgrading. Startup validation in
    /// <c>AiServiceExtensions</c> turns that latent 403 into a refusal to boot, because per-member
    /// inference failures are swallowed and a silent 403 would look like "no assessments due".
    /// </remarks>
    public bool UseIdentityToken { get; set; }
}
