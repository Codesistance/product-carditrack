namespace CardiTrack.Application.Exceptions;

/// <summary>
/// Raised when a structured model reply stopped at the output-token ceiling instead of finishing,
/// so what came back is a cut-off document rather than an answer.
/// </summary>
/// <remarks>
/// <para>
/// Its own type, and one a caller can act on, because the two things this can mean want opposite
/// responses. A reply that needed a little more room than the ceiling allows is a configuration
/// fault: raise the ceiling and it finishes. A reply that fills the whole ceiling when the
/// operation usually takes a few hundred tokens is a model that did not stop — a repetition loop
/// inside the grammar-constrained decode — and no ceiling is high enough for that. The numbers
/// this carries are what tells the two apart, and a caller that sees the second shape can stop
/// spending inference on the same prompt (see <c>DigestGenerationService</c>'s hold) rather than
/// retry into the same loop on every pass.
/// </para>
/// <para>
/// Derives from <see cref="HttpRequestException"/> so that every existing catch of a model-call
/// failure — the API's chat endpoint answering 503, the pipeline logging one member's failure and
/// moving on — keeps treating it as the upstream failure it is. The message carries token counts
/// only, never any of the reply: the same privacy invariant as every other error the model
/// clients raise.
/// </para>
/// </remarks>
public class AiReplyTruncatedException : HttpRequestException
{
    public AiReplyTruncatedException(
        string message,
        int outputTokens,
        int maxOutputTokens,
        int? inputTokens,
        int contextTokens)
        : base(message)
    {
        OutputTokens = outputTokens;
        MaxOutputTokens = maxOutputTokens;
        InputTokens = inputTokens;
        ContextTokens = contextTokens;
    }

    /// <summary>Tokens the model produced before it was stopped — equal to the ceiling by definition.</summary>
    public int OutputTokens { get; }

    /// <summary>The output ceiling in force for the call (<c>num_predict</c>).</summary>
    public int MaxOutputTokens { get; }

    /// <summary>Prompt tokens, when the server reported them.</summary>
    public int? InputTokens { get; }

    /// <summary>The context window the prompt and the reply shared (<c>num_ctx</c>).</summary>
    public int ContextTokens { get; }
}
