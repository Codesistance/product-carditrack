namespace CardiTrack.Infrastructure.Settings;

/// <summary>
/// The part of a settings class <see cref="ExternalClients.Medical.MedGemmaClient"/> actually
/// reads — <c>Model</c> per call and <c>TimeoutSeconds</c> for its error messages. Everything
/// else (<c>BaseUrl</c>, <c>UseIdentityToken</c>) is consumed outside the client, when wiring the
/// named <see cref="HttpClient"/> it is handed.
/// </summary>
/// <remarks>
/// Exists so <see cref="PrivateAiSettings"/> and <see cref="RewriteAiSettings"/> can share one
/// <see cref="ExternalClients.Medical.MedGemmaClient"/> implementation — two model tags on the
/// same in-project host — without the client depending on either settings type by name.
/// </remarks>
public interface IMedGemmaModelSettings
{
    string Model { get; }
    int TimeoutSeconds { get; }

    /// <summary>
    /// Ollama's <c>num_ctx</c> — the window prompt and completion share. Sent explicitly because
    /// the alternative is not "no limit", it is whatever the server happens to default to: a
    /// window sized for a chat turn silently truncates a long structured reply mid-token, and the
    /// only evidence is a JSON parse error at whatever byte the cut landed on.
    /// </summary>
    int ContextTokens { get; }

    /// <summary>
    /// Ollama's <c>num_predict</c> — the ceiling on one completion, within
    /// <see cref="ContextTokens"/>. Names the output budget in its own right rather than leaving
    /// it as whatever is left over after the prompt, so a prompt that grows takes room from
    /// nothing.
    /// </summary>
    int MaxOutputTokens { get; }
}
