using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CardiTrack.Application.DTOs.Common;
using CardiTrack.Application.Interfaces.Clients;
using CardiTrack.Infrastructure.Diagnostics;
using CardiTrack.Shared.Json;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace CardiTrack.Infrastructure.ExternalClients.Vertex;

/// <summary>
/// Client for Gemini models served by Vertex AI (<c>aiplatform.googleapis.com</c>), always through
/// a regional endpoint so processing stays in the region the DPIA names. Auth is IAM via
/// <see cref="VertexAccessTokenHandler"/>, never an API key.
///
/// Privacy invariant (DPIA): prompts and completions can carry health-derived text — the Rewrite
/// slot sends the caregiver's question and MedGemma's clinical read through this client. No prompt
/// text, no model output and no response-body fragment may ever reach a log, span attribute,
/// metric tag or exception message produced by this class. Token counts, durations, model names,
/// status codes, JSON error positions and finish/block reason enum values are the only telemetry
/// payload.
/// </summary>
public class VertexAiClient : IExternalAiClient
{
    private const string ProviderName = "gcp.vertex_ai";

    /// <summary>
    /// Attempts per logical call, including the first. Covers an IAM binding still propagating
    /// after a redeploy or a transient front-end error — both resolve within seconds. It will not
    /// mask a sustained outage: a misconfiguration that fails every attempt still surfaces as an
    /// error, just a few seconds later.
    /// </summary>
    private const int MaxAttempts = 3;

    /// <summary>Backoff step for a rejection that clears on its own within seconds.</summary>
    private const int TransientBackoffSeconds = 2;

    /// <summary>
    /// Backoff step for a 429/503 — on Vertex that is a per-minute quota window or regional
    /// capacity, and what clears it is the window rolling over, not an immediate re-ask.
    /// </summary>
    private const int SaturationBackoffSeconds = 15;

    /// <summary>Ceiling on a server-advised <c>Retry-After</c>.</summary>
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Categories a Gemini response can be blocked under, each set to intervene only on
    /// high-probability harm. The default (BLOCK_MEDIUM_AND_ABOVE on some models) is tuned for
    /// consumer chat; here the "user" content is a caregiver's health question plus clinical
    /// prose, which safety filters can misread — dangerous-content in particular triggers on
    /// medical language. A block still surfaces as an error (never silently empty), tagged with
    /// the category enum only.
    /// </summary>
    private static readonly string[] SafetyCategories =
    [
        "HARM_CATEGORY_HARASSMENT",
        "HARM_CATEGORY_HATE_SPEECH",
        "HARM_CATEGORY_SEXUALLY_EXPLICIT",
        "HARM_CATEGORY_DANGEROUS_CONTENT",
    ];

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly VertexAiClientOptions _options;
    private readonly string _httpClientName;
    private readonly ILogger<VertexAiClient> _logger;
    private readonly TimeProvider _timeProvider;

    public VertexAiClient(
        IHttpClientFactory httpClientFactory,
        VertexAiClientOptions options,
        string httpClientName,
        ILogger<VertexAiClient> logger,
        TimeProvider? timeProvider = null)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _httpClientName = httpClientName;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<string> GenerateAsync(string prompt, CancellationToken ct = default)
        => (await GenerateWithUsageAsync(prompt, ct)).Result;

    public async Task<string> ChatAsync(IReadOnlyList<ChatMessage> history, string userMessage, CancellationToken ct = default)
    {
        var contents = history
            .Select(m => new VertexContent
            {
                Role = m.Role == ChatRole.User ? "user" : "model",
                Parts = [new VertexPart { Text = m.Content }]
            })
            .Append(new VertexContent { Role = "user", Parts = [new VertexPart { Text = userMessage }] })
            .ToList();
        var (result, _) = await SendInstrumentedCoreAsync(
            operationName: "chat",
            request: BuildRequest(contents, responseSchemaText: null),
            parseContent: content => content,
            ct);
        return result;
    }

    /// <inheritdoc cref="IExternalAiClient.GenerateWithUsageAsync"/>
    public async Task<AiGenerationResult<string>> GenerateWithUsageAsync(string prompt, CancellationToken ct = default)
    {
        var (result, usage) = await SendInstrumentedCoreAsync(
            operationName: "generate_content",
            request: BuildRequest(SingleUserTurn(prompt), responseSchemaText: null),
            parseContent: content => content,
            ct);
        return new AiGenerationResult<string>(result, usage);
    }

    public async Task<T> GenerateStructuredAsync<T>(string prompt, CancellationToken ct = default) where T : class
        => (await GenerateStructuredWithUsageAsync<T>(prompt, ct)).Result;

    /// <summary>
    /// The JSON Schema for <typeparamref name="T"/> is the single source of truth for two things
    /// at once: it is set as <c>generationConfig.responseJsonSchema</c> (constrained decoding —
    /// the model cannot produce a reply outside the shape), and its text is appended to the prompt
    /// so the model is also told in plain terms what is expected of it. One drifting out of sync
    /// with the other is not possible because both come from the same generated text — the same
    /// contract <see cref="Medical.MedGemmaClient"/> keeps with Ollama's <c>format</c> field.
    /// </summary>
    public async Task<AiGenerationResult<T>> GenerateStructuredWithUsageAsync<T>(
        string prompt, CancellationToken ct = default) where T : class
    {
        var schemaText = StructuredOutputSchema.TextFor<T>();
        var fullPrompt = StructuredOutputSchema.PromptWithSchema(prompt, schemaText);
        var (result, usage) = await SendInstrumentedCoreAsync(
            operationName: "generate_structured",
            request: BuildRequest(SingleUserTurn(fullPrompt), schemaText),
            parseContent: content => DeserializeStructured<T>(content, "generate_structured"),
            ct);
        return new AiGenerationResult<T>(result, usage);
    }

    private static List<VertexContent> SingleUserTurn(string prompt) =>
        [new VertexContent { Role = "user", Parts = [new VertexPart { Text = prompt }] }];

    private VertexRequest BuildRequest(List<VertexContent> contents, string? responseSchemaText) => new()
    {
        Contents = contents,
        GenerationConfig = new VertexGenerationConfig
        {
            MaxOutputTokens = _options.MaxOutputTokens,
            ResponseMimeType = responseSchemaText is null ? null : "application/json",
            ResponseJsonSchema = responseSchemaText is null ? null : JsonNode.Parse(responseSchemaText),
        },
        SafetySettings = SafetyCategories
            .Select(category => new VertexSafetySetting { Category = category })
            .ToList(),
    };

    /// <summary>Same contract as <see cref="Medical.MedGemmaClient"/>'s structured parse: a
    /// malformed reply must not leak into a log or exception — positions only, never content.</summary>
    private T DeserializeStructured<T>(string content, string operationName) where T : class
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<T>(content, StructuredOutputSchema.SerializerOptions)
                ?? throw new System.Text.Json.JsonException("Deserialized structured response was null.");
        }
        catch (System.Text.Json.JsonException ex)
        {
            _logger.LogError(
                "Vertex {Operation} returned structured content that could not be parsed into "
                + "{Type}: error at '{Path}' (line {Line}, byte {BytePosition}); body length {BodyLength} chars",
                operationName, typeof(T).Name, ex.Path ?? "$", ex.LineNumber ?? 0, ex.BytePositionInLine ?? 0,
                content.Length);
            throw new HttpRequestException(
                $"Vertex {operationName} returned structured content that could not be parsed into "
                + $"{typeof(T).Name} (error at '{ex.Path ?? "$"}', line {ex.LineNumber ?? 0}, "
                + $"byte {ex.BytePositionInLine ?? 0}).");
        }
    }

    /// <summary>
    /// The single instrumented path every operation goes through — the same span/metric/log
    /// discipline as <see cref="Medical.MedGemmaClient"/>, which documents the reasoning. The
    /// notable Vertex-specific outcome is a <em>blocked</em> response: HTTP 200 with no usable
    /// candidate and a block or finish reason enum. That surfaces as an error carrying the enum
    /// value only, because an empty string handed to member chat would read as the model having
    /// nothing to say rather than the platform having refused to say it.
    /// </summary>
    private async Task<(TResult Result, AiUsage Usage)> SendInstrumentedCoreAsync<TResult>(
        string operationName,
        VertexRequest request,
        Func<string, TResult> parseContent,
        CancellationToken ct)
    {
        using var activity = AiTelemetry.Source.StartActivity(
            $"{operationName} {_options.Model}", ActivityKind.Client);
        activity?.SetTag(AiTelemetry.OperationNameTag, operationName);
        activity?.SetTag(AiTelemetry.ProviderNameTag, ProviderName);
        activity?.SetTag(AiTelemetry.SystemTag, ProviderName);
        activity?.SetTag(AiTelemetry.RequestModelTag, _options.Model);

        var stopwatch = Stopwatch.StartNew();
        string? errorType = null;
        try
        {
            var client = _httpClientFactory.CreateClient(_httpClientName);
            var path = $"/v1/projects/{_options.ProjectId}/locations/{_options.Location}"
                + $"/publishers/google/models/{_options.Model}:generateContent";
            var body = await SendWithRetryAsync(
                client, (c, token) => c.PostAsJsonAsync(path, request, token), operationName, stopwatch, ct);

            // Lenient parse on purpose: the strict JsonUtility.Deserialize throws a
            // JsonDeserializationException whose message embeds a 1000-char payload preview —
            // here that preview is model output derived from health data, so it must never be
            // constructed. Error paths and positions are safe to report.
            if (!JsonUtility.TryDeserialize<VertexGenerateContentResponse>(body, out var parsed, out var errors) || parsed is null)
            {
                errorType = "invalid_response";
                var first = errors.Count > 0 ? errors[0] : null;
                _logger.LogError(
                    "Vertex {Operation} returned a response that could not be parsed: "
                    + "{ErrorCount} JSON error(s), first at '{Path}' (line {Line}, pos {Position}); "
                    + "body length {BodyLength} chars",
                    operationName, errors.Count, first?.Path is { Length: > 0 } path1 ? path1 : "$",
                    first?.LineNumber ?? 0, first?.LinePosition ?? 0, body.Length);
                throw new HttpRequestException(
                    $"Vertex {operationName} returned a response that could not be parsed "
                    + $"({errors.Count} JSON error(s), first at '{(first?.Path is { Length: > 0 } p ? p : "$")}', "
                    + $"line {first?.LineNumber ?? 0}, pos {first?.LinePosition ?? 0}).");
            }

            var usageMeta = parsed.UsageMetadata;
            activity?.SetTag(AiTelemetry.ResponseModelTag, parsed.ModelVersion);
            if (usageMeta?.PromptTokenCount is { } inputTokens)
            {
                activity?.SetTag(AiTelemetry.InputTokensTag, inputTokens);
                AiTelemetry.TokenUsage.Record(inputTokens, TokenTags(operationName, "input"));
            }
            // Thinking tokens are generated output and billed as such, so they fold into the
            // output count rather than disappearing from the persisted per-turn usage.
            var outputTokens = (usageMeta?.CandidatesTokenCount, usageMeta?.ThoughtsTokenCount) switch
            {
                (null, null) => (int?)null,
                var (candidates, thoughts) => (candidates ?? 0) + (thoughts ?? 0),
            };
            if (outputTokens is { } output)
            {
                activity?.SetTag(AiTelemetry.OutputTokensTag, output);
                AiTelemetry.TokenUsage.Record(output, TokenTags(operationName, "output"));
            }

            ThrowIfBlocked(parsed, operationName, ref errorType);

            var candidate = parsed.Candidates?.FirstOrDefault();
            var content = string.Concat(
                (candidate?.Content?.Parts ?? [])
                    .Where(part => part.Thought != true)
                    .Select(part => part.Text ?? string.Empty));

            if (string.IsNullOrEmpty(content))
            {
                _logger.LogWarning(
                    "Vertex {Operation} returned empty content (finish_reason {FinishReason})",
                    operationName, candidate?.FinishReason);
            }

            _logger.LogInformation(
                "Vertex {Operation} completed: model {Model}, {ElapsedMs} ms, "
                + "tokens in {InputTokens} out {OutputTokens} (thoughts {ThoughtsTokens}), "
                + "finish_reason {FinishReason}, trace {TraceId}",
                operationName, parsed.ModelVersion ?? _options.Model, stopwatch.ElapsedMilliseconds,
                usageMeta?.PromptTokenCount, outputTokens, usageMeta?.ThoughtsTokenCount,
                candidate?.FinishReason, (activity ?? Activity.Current)?.TraceId.ToString());

            var usage = new AiUsage
            {
                ModelName = parsed.ModelVersion ?? _options.Model,
                InputTokens = usageMeta?.PromptTokenCount,
                OutputTokens = outputTokens,
                DurationMs = stopwatch.ElapsedMilliseconds,
            };
            return (parseContent(content), usage);
        }
        catch (Exception ex)
        {
            // No AddException/RecordException: exception messages can embed payload fragments,
            // and the type alone is what dashboards aggregate on.
            errorType ??= ex is HttpRequestException { StatusCode: { } statusCode }
                ? ((int)statusCode).ToString()
                : ex.GetType().FullName;
            activity?.SetStatus(ActivityStatusCode.Error, errorType);
            activity?.SetTag(AiTelemetry.ErrorTypeTag, errorType);
            throw;
        }
        finally
        {
            var durationTags = TokenlessTags(operationName);
            if (errorType is not null)
                durationTags.Add(AiTelemetry.ErrorTypeTag, errorType);
            AiTelemetry.OperationDuration.Record(stopwatch.Elapsed.TotalSeconds, durationTags);
        }
    }

    /// <summary>
    /// Converts a platform refusal into an error carrying only the reason enum. Two forms exist:
    /// the prompt itself blocked (<c>promptFeedback.blockReason</c>, no candidates at all), and a
    /// generation stopped for a safety-class reason (<c>finishReason</c> beyond STOP/MAX_TOKENS).
    /// The enum values are Google's fixed vocabulary, not content, so they are safe to log and tag.
    /// </summary>
    private void ThrowIfBlocked(VertexGenerateContentResponse response, string operationName, ref string? errorType)
    {
        var blockReason = response.PromptFeedback?.BlockReason;
        if (!string.IsNullOrEmpty(blockReason) && blockReason != "BLOCK_REASON_UNSPECIFIED")
        {
            errorType = $"blocked_{blockReason}";
            _logger.LogError(
                "Vertex {Operation} blocked the prompt (block_reason {BlockReason})",
                operationName, blockReason);
            throw new HttpRequestException(
                $"Vertex {operationName} blocked the prompt (block reason {blockReason}).");
        }

        var finishReason = response.Candidates?.FirstOrDefault()?.FinishReason;
        if (finishReason is "SAFETY" or "PROHIBITED_CONTENT" or "RECITATION" or "BLOCKLIST" or "SPII")
        {
            errorType = $"blocked_{finishReason}";
            _logger.LogError(
                "Vertex {Operation} stopped the generation (finish_reason {FinishReason})",
                operationName, finishReason);
            throw new HttpRequestException(
                $"Vertex {operationName} stopped the generation (finish reason {finishReason}).");
        }
    }

    /// <summary>
    /// Same retry contract as <see cref="Medical.MedGemmaClient"/>, which documents the reasoning:
    /// only the HTTP outcome is retried, a 200 with an unusable body is not, and a client-side
    /// timeout is terminal. The error body of a failed attempt is never read — Vertex error
    /// payloads can echo request fragments in their message field.
    /// </summary>
    private async Task<string> SendWithRetryAsync(
        HttpClient client,
        Func<HttpClient, CancellationToken, Task<HttpResponseMessage>> send,
        string operationName,
        Stopwatch stopwatch,
        CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var response = await SendOnceAsync(client, send, operationName, stopwatch, ct);
            if (response.IsSuccessStatusCode)
            {
                if (attempt > 1)
                {
                    _logger.LogWarning(
                        "Vertex {Operation} succeeded on attempt {Attempt} of {MaxAttempts} "
                        + "after a transient failure.",
                        operationName, attempt, MaxAttempts);
                }
                return await response.Content.ReadAsStringAsync(ct);
            }

            var statusCode = response.StatusCode;
            if (attempt >= MaxAttempts)
            {
                _logger.LogError(
                    "Vertex {Operation} failed: HTTP {StatusCode} after {ElapsedMs} ms ({Attempts} attempt(s))",
                    operationName, (int)statusCode, stopwatch.ElapsedMilliseconds, attempt);
                throw new HttpRequestException(
                    $"Vertex {operationName} returned HTTP {(int)statusCode}.",
                    inner: null, statusCode: statusCode);
            }

            var backoff = BackoffFor(statusCode, response.Headers.RetryAfter, attempt);

            // Released before the wait, not after it — see MedGemmaClient.SendWithRetryAsync.
            response.Dispose();
            await Task.Delay(backoff, _timeProvider, ct);
        }
    }

    /// <summary>
    /// One attempt, with the client-side timeout separated from the caller giving up — both
    /// surface as <see cref="TaskCanceledException"/>, and the distinction is the diagnosis.
    /// Terminal on purpose, same as the Ollama client: re-sending a request the far side is still
    /// working on adds load to the thing that was already too slow.
    /// </summary>
    private async Task<HttpResponseMessage> SendOnceAsync(
        HttpClient client,
        Func<HttpClient, CancellationToken, Task<HttpResponseMessage>> send,
        string operationName,
        Stopwatch stopwatch,
        CancellationToken ct)
    {
        try
        {
            return await send(client, ct);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogError(
                "Vertex {Operation} timed out after {ElapsedMs} ms "
                + "(HttpClient.Timeout is {TimeoutSeconds} s)",
                operationName, stopwatch.ElapsedMilliseconds, _options.TimeoutSeconds);
            throw new TimeoutException(
                $"Vertex {operationName} timed out after {_options.TimeoutSeconds} s.", ex);
        }
    }

    /// <summary>
    /// How long to wait before the next attempt. A server-advised <c>Retry-After</c> is honoured
    /// (capped at <see cref="MaxBackoff"/>); absent that, 429/503 wait for a quota window to roll
    /// over and everything else takes the short transient step.
    /// </summary>
    private TimeSpan BackoffFor(HttpStatusCode statusCode, RetryConditionHeaderValue? retryAfter, int attempt)
    {
        if (AdvisedDelay(retryAfter) is { } advised)
            return advised > MaxBackoff ? MaxBackoff : advised;

        var seconds = statusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable
            ? attempt * SaturationBackoffSeconds
            : attempt * TransientBackoffSeconds;
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>
    /// The <c>Retry-After</c> value as a delay, in either of the forms RFC 9110 allows: a delta
    /// in seconds, or an HTTP date. A date already in the past yields <see cref="TimeSpan.Zero"/>
    /// rather than a negative wait.
    /// </summary>
    private TimeSpan? AdvisedDelay(RetryConditionHeaderValue? retryAfter)
    {
        if (retryAfter is null)
            return null;

        if (retryAfter.Delta is { } delta)
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;

        if (retryAfter.Date is { } date)
        {
            var until = date - _timeProvider.GetUtcNow();
            return until < TimeSpan.Zero ? TimeSpan.Zero : until;
        }

        return null;
    }

    private TagList TokenTags(string operationName, string tokenType)
    {
        var tags = TokenlessTags(operationName);
        tags.Add(AiTelemetry.TokenTypeTag, tokenType);
        return tags;
    }

    private TagList TokenlessTags(string operationName) => new()
    {
        { AiTelemetry.OperationNameTag, operationName },
        { AiTelemetry.ProviderNameTag, ProviderName },
        { AiTelemetry.RequestModelTag, _options.Model },
    };

    // Requests serialize through PostAsJsonAsync = System.Text.Json, so request records use
    // [JsonPropertyName]. Responses deserialize through JsonUtility = Newtonsoft, so response
    // records must use [JsonProperty] — mixing them up fails silently (see MedGemmaClient).

    private record VertexRequest
    {
        [JsonPropertyName("contents")] public required List<VertexContent> Contents { get; init; }
        [JsonPropertyName("generationConfig")] public required VertexGenerationConfig GenerationConfig { get; init; }
        [JsonPropertyName("safetySettings")] public required List<VertexSafetySetting> SafetySettings { get; init; }
    }

    private record VertexContent
    {
        [JsonPropertyName("role")] public required string Role { get; init; }
        [JsonPropertyName("parts")] public required List<VertexPart> Parts { get; init; }
    }

    private record VertexPart
    {
        [JsonPropertyName("text")] public required string Text { get; init; }
    }

    private record VertexGenerationConfig
    {
        [JsonPropertyName("maxOutputTokens")] public required int MaxOutputTokens { get; init; }

        /// <summary>
        /// Always zero: this platform's callers want the answer, not a reasoning trace — the
        /// Rewrite slot in particular runs under interactive budgets (member chat's waiting
        /// sentences allow 20 s end to end). The same decision as the Ollama client's
        /// <c>think:false</c>, in Vertex's vocabulary. Flash-tier models accept 0; a model that
        /// rejects it (e.g. the Pro tier) is not a valid target for these slots.
        /// </summary>
        [JsonPropertyName("thinkingConfig")] public VertexThinkingConfig ThinkingConfig { get; init; } = new();

        [JsonPropertyName("responseMimeType")]
        [System.Text.Json.Serialization.JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ResponseMimeType { get; init; }

        /// <summary>A JSON Schema — Vertex's constrained decoding. Null for a free-text call.</summary>
        [JsonPropertyName("responseJsonSchema")]
        [System.Text.Json.Serialization.JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public JsonNode? ResponseJsonSchema { get; init; }
    }

    private record VertexThinkingConfig
    {
        [JsonPropertyName("thinkingBudget")] public int ThinkingBudget { get; init; }
    }

    private record VertexSafetySetting
    {
        [JsonPropertyName("category")] public required string Category { get; init; }
        [JsonPropertyName("threshold")] public string Threshold { get; init; } = "BLOCK_ONLY_HIGH";
    }

    private sealed record VertexGenerateContentResponse
    {
        [JsonProperty("candidates")] public List<VertexCandidate>? Candidates { get; init; }
        [JsonProperty("promptFeedback")] public VertexPromptFeedback? PromptFeedback { get; init; }
        [JsonProperty("usageMetadata")] public VertexUsageMetadata? UsageMetadata { get; init; }
        [JsonProperty("modelVersion")] public string? ModelVersion { get; init; }
    }

    private sealed record VertexCandidate
    {
        [JsonProperty("content")] public VertexResponseContent? Content { get; init; }
        [JsonProperty("finishReason")] public string? FinishReason { get; init; }
    }

    private sealed record VertexResponseContent
    {
        [JsonProperty("parts")] public List<VertexResponsePart>? Parts { get; init; }
    }

    private sealed record VertexResponsePart
    {
        [JsonProperty("text")] public string? Text { get; init; }

        /// <summary>True on parts carrying a thought summary rather than the answer.</summary>
        [JsonProperty("thought")] public bool? Thought { get; init; }
    }

    private sealed record VertexPromptFeedback
    {
        [JsonProperty("blockReason")] public string? BlockReason { get; init; }
    }

    /// <summary>All nullable: absent fields must degrade to absent tags, not throw.</summary>
    private sealed record VertexUsageMetadata
    {
        [JsonProperty("promptTokenCount")] public int? PromptTokenCount { get; init; }
        [JsonProperty("candidatesTokenCount")] public int? CandidatesTokenCount { get; init; }
        [JsonProperty("thoughtsTokenCount")] public int? ThoughtsTokenCount { get; init; }
        [JsonProperty("totalTokenCount")] public int? TotalTokenCount { get; init; }
    }
}
