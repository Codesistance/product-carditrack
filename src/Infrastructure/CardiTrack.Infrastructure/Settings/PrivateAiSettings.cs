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

    /// <summary>
    /// <inheritdoc cref="IMedGemmaModelSettings.ContextTokens" path="/summary"/>
    /// </summary>
    /// <remarks>
    /// 8192 rather than the 4096 an unconfigured Ollama serves: the clinical prompts on this slot
    /// carry a day of readings, the family's questionnaire answers and the reply schema, and at
    /// 4096 a digest ran out of window part-way through its first field. The cost is KV cache —
    /// it scales with this number, and this model is served on CPU-backed Cloud Run — so it is a
    /// setting, not a constant: an environment that measures memory pressure lowers it here
    /// rather than in code.
    /// </remarks>
    public int ContextTokens { get; set; } = 8192;

    /// <summary>
    /// <inheritdoc cref="IMedGemmaModelSettings.MaxOutputTokens" path="/summary"/>
    /// </summary>
    /// <remarks>
    /// 2048 is several times what any prompt on this slot actually asks for — a digest is a few
    /// sentences and some short fields — because this ceiling is not the place to enforce
    /// brevity. The prompt and the reply schema ask for that, and a reply that ignores them is
    /// rejected downstream on its merits; cutting it off here would instead produce truncated
    /// JSON, which is unreadable rather than merely too long.
    /// </remarks>
    public int MaxOutputTokens { get; set; } = 2048;

    // CurrentStatusBudgetSeconds was removed with the batch move: the status line is generated
    // by the pipeline (StatusLineGenerationService) and served from its persisted row, so no
    // request waits on a generation and the budget has nothing left to protect.

    /// <summary>
    /// Whether an arriving caregiver may trigger a model load ahead of their first question —
    /// see <c>MedGemmaWarmUpService</c>. On by default: the deployed service runs at
    /// <c>min_instance_count = 0</c>, so without this the first interactive call after an idle
    /// spell waits ~54 s for the weights to be read off disk
    /// (docs/technical/medgemma_serving_architecture.md §9.1a).
    /// </summary>
    /// <remarks>
    /// A switch rather than a constant because the cost is real and asymmetric: warming is what
    /// keeps the GPU instance up, and an environment that would rather pay the wait than the
    /// seconds — or one whose model is local and loads instantly — turns it off with one variable.
    /// </remarks>
    public bool WarmUpEnabled { get; set; } = true;

    /// <summary>
    /// <inheritdoc cref="IMedGemmaModelSettings.LogClinicalOutput" path="/summary"/>
    /// </summary>
    /// <remarks>
    /// <c>AI__Private__LogClinicalOutput</c>. Terraform sets it true for dev and false for prod;
    /// locally it is whatever appsettings says. A true value in a host whose
    /// <c>ASPNETCORE_ENVIRONMENT</c> is Prod stops that host from starting rather than being
    /// quietly ignored — a misconfiguration that would log health data must be loud.
    /// </remarks>
    public bool LogClinicalOutput { get; set; }

    /// <summary>
    /// Floor on how often one host will actually issue a warm-up, however many arrivals ask for
    /// one. Five minutes: long enough that a burst of morning app-opens costs a single load,
    /// short enough to stay well inside Cloud Run's idle scale-in, so a caregiver who comes back
    /// after a gap still finds the instance warm.
    /// </summary>
    public int WarmUpMinimumIntervalSeconds { get; set; } = 300;

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
