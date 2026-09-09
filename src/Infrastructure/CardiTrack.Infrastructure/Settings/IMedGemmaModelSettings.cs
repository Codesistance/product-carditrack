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

    /// <summary>
    /// Ollama's <c>repeat_penalty</c> — how strongly a token already in the recent window is
    /// discouraged from being produced again. 1.0 is off. Sent explicitly because the server's
    /// own default (1.1) was measured too weak to stop a 4B model that has started restating
    /// itself inside a grammar-constrained string field, where nothing in the grammar can end
    /// the sentence for it.
    /// </summary>
    double RepeatPenalty { get; }

    /// <summary>
    /// Ollama's <c>repeat_last_n</c> — how many recent tokens <see cref="RepeatPenalty"/> looks
    /// back over. 0 disables it; -1 means the whole context window. Sent explicitly because the
    /// server's default (64) is shorter than the block a looping clinical read repeats, so the
    /// penalty never saw the repetition it exists to stop.
    /// </summary>
    int RepeatLastN { get; }

    /// <summary>
    /// Inspection switch: when true, <see cref="ExternalClients.Medical.MedGemmaClient"/> writes
    /// every prompt it sends and every completion it receives to the log, verbatim. This is the
    /// one sanctioned exception to the client's privacy invariant, exists so the clinical output
    /// can be read during development, and is refused at start-up in a production environment —
    /// see <c>AiServiceExtensions.RequireNoClinicalLoggingInProduction</c>. Default false.
    /// </summary>
    bool LogClinicalOutput { get; }
}
