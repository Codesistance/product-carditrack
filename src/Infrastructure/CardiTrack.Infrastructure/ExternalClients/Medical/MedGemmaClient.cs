using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CardiTrack.Application.DTOs.Common;
using CardiTrack.Application.Interfaces.Clients;
using CardiTrack.Infrastructure.Diagnostics;
using CardiTrack.Infrastructure.Settings;
using CardiTrack.Shared.Json;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace CardiTrack.Infrastructure.ExternalClients.Medical;

/// <summary>
/// Client for the in-VPC MedGemma service (stock Ollama on Cloud Run). The container cannot
/// be instrumented, so every observability signal for a MedGemma call — span, metrics, log
/// line — is emitted here, client-side, via <see cref="AiTelemetry"/>.
///
/// Privacy invariant (DPIA): prompts and completions carry health data. No prompt text, no
/// model output and no response-body fragment may ever reach a log, span attribute, metric
/// tag or exception message produced by this class. Token counts, durations, model names,
/// status codes and JSON error positions are the only telemetry payload.
///
/// The one exception is <see cref="IMedGemmaModelSettings.LogClinicalOutput"/>: an inspection
/// switch that writes prompts and completions verbatim to the log so the clinical output can be
/// read during development. It is off by default, the settings loader refuses to start a
/// production host with it on, and every line it writes is tagged
/// <see cref="ClinicalInspectionEvent"/> so it can be found — and dropped — by event id.
/// </summary>
public class MedGemmaClient : IExternalAiClient, IAiWarmUpClient
{
    private const string ProviderName = "ollama";

    /// <summary>
    /// Attempts per logical call, including the first. Covers a Cloud Run instance still
    /// spinning up or an IAM binding still propagating after a redeploy — both resolve within
    /// seconds. It will not mask a sustained outage: a misconfiguration that fails every
    /// attempt still surfaces as an error, just a few seconds later than today.
    /// </summary>
    private const int MaxAttempts = 3;

    /// <summary>Backoff step for a rejection that clears on its own within seconds.</summary>
    private const int ColdStartBackoffSeconds = 2;

    /// <summary>
    /// Backoff step for a rejection that means the far side is full — see <see cref="BackoffFor"/>.
    /// </summary>
    private const int SaturationBackoffSeconds = 15;

    /// <summary>Ceiling on a server-advised <c>Retry-After</c>.</summary>
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Ollama's <c>done_reason</c> for a generation that hit the token ceiling instead of
    /// stopping on its own. Its counterparts are <c>stop</c> (finished) and <c>load</c>
    /// (<see cref="WarmUpAsync"/>'s preload, which produces no completion at all).
    /// </summary>
    private const string LengthDoneReason = "length";

    /// <summary>
    /// Event id on every verbatim prompt or completion written under
    /// <see cref="IMedGemmaModelSettings.LogClinicalOutput"/>, so those lines can be filtered
    /// to — or filtered out — without a text match on health data.
    /// </summary>
    public static readonly EventId ClinicalInspectionEvent = new(4200, "ClinicalInspection");

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMedGemmaModelSettings _settings;
    private readonly string _httpClientName;
    private readonly ILogger<MedGemmaClient> _logger;
    private readonly TimeProvider _timeProvider;

    /// <remarks>
    /// Takes <see cref="IMedGemmaModelSettings"/> rather than <see cref="PrivateAiSettings"/>
    /// directly so the same client type serves both the Private and Rewrite slots — two model
    /// tags on the same in-project Ollama host (see <c>AiServiceExtensions.AddMedicalAiServices</c>).
    /// </remarks>
    public MedGemmaClient(
        IHttpClientFactory httpClientFactory,
        IMedGemmaModelSettings settings,
        string httpClientName,
        ILogger<MedGemmaClient> logger,
        TimeProvider? timeProvider = null)
    {
        _httpClientFactory = httpClientFactory;
        _settings = settings;
        _httpClientName = httpClientName;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<string> GenerateAsync(string prompt, CancellationToken ct = default)
        => (await GenerateWithUsageAsync(prompt, ct)).Result;

    public async Task<string> ChatAsync(IReadOnlyList<ChatMessage> history, string userMessage, CancellationToken ct = default)
    {
        var messages = history
            .Select(m => new OllamaMessage { Role = m.Role == ChatRole.User ? "user" : "assistant", Content = m.Content })
            .Append(new OllamaMessage { Role = "user", Content = userMessage })
            .ToList();
        var request = new OllamaChatRequest { Model = _settings.Model, Messages = messages, Options = TokenBudget() };
        // The whole transcript, because that is what Ollama is sent: a chat prompt is every
        // turn so far, not the latest one. Role-prefixed so a log reader can follow it.
        if (_settings.LogClinicalOutput)
        {
            var transcript = string.Join(
                "\n\n",
                messages.Select(m => $"[{m.Role}] {m.Content}"));
            LogClinicalText("chat", $"prompt ({messages.Count} message(s), full transcript)", transcript);
        }
        var (result, _) = await SendInstrumentedCoreAsync<OllamaChatResponse, string>(
            operationName: "chat",
            send: (client, token) => client.PostAsJsonAsync("/api/chat", request, token),
            selectContent: response => response.Message?.Content,
            parseContent: content => content,
            ct);
        return result;
    }

    /// <inheritdoc cref="CardiTrack.Application.Interfaces.Clients.IExternalAiClient.GenerateWithUsageAsync"/>
    public async Task<CardiTrack.Application.DTOs.Common.AiGenerationResult<string>> GenerateWithUsageAsync(
        string prompt, CancellationToken ct = default)
    {
        var request = new OllamaGenerateRequest { Model = _settings.Model, Prompt = prompt, Options = TokenBudget() };
        LogClinicalText("generate_content", "prompt", prompt);
        var (result, usage) = await SendInstrumentedCoreAsync<OllamaGenerateResponse, string>(
            operationName: "generate_content",
            send: (client, token) => client.PostAsJsonAsync("/api/generate", request, token),
            selectContent: response => response.Response,
            parseContent: content => content,
            ct);
        return new CardiTrack.Application.DTOs.Common.AiGenerationResult<string>(result, usage);
    }

    /// <summary>
    /// Loads the model without asking it for anything — Ollama's documented preload: a
    /// <c>/api/generate</c> with an empty prompt returns as soon as the weights are resident,
    /// with <c>done_reason: "load"</c> and no completion.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It goes through the same instrumented path as every other call on purpose: a warm-up is
    /// the one request whose whole point is how long it takes, so it belongs on the same span and
    /// duration metric as the calls it exists to make faster — filterable by
    /// <c>gen_ai.operation.name = warm_up</c>. Empty content is the success shape here rather
    /// than the "the model answered with nothing" warning it is everywhere else, which is what
    /// <c>allowEmptyContent</c> says.
    /// </para>
    /// <para>
    /// One attempt, unlike every other operation. <see cref="MaxAttempts"/> exists for a caller
    /// who needs an answer; this one does not. Worse, the service admits a single request at a
    /// time, so a warm-up that retried a 429 would be queueing behind — and then ahead of — the
    /// real calls it exists to speed up. And a 429 has already answered the only question a
    /// warm-up asks: something is being served, so the model is loaded.
    /// </para>
    /// </remarks>
    public async Task WarmUpAsync(CancellationToken ct = default)
    {
        var request = new OllamaGenerateRequest { Model = _settings.Model, Prompt = string.Empty, Options = TokenBudget() };
        await SendInstrumentedCoreAsync<OllamaGenerateResponse, string>(
            operationName: "warm_up",
            send: (client, token) => client.PostAsJsonAsync("/api/generate", request, token),
            selectContent: response => response.Response,
            parseContent: content => content,
            ct,
            allowEmptyContent: true,
            maxAttempts: 1);
    }

    /// <summary>
    /// The JSON Schema for <typeparamref name="T"/> is the single source of truth for two things
    /// at once: it is set as Ollama's <c>format</c> field (grammar-constrained decoding — the model
    /// cannot produce a token sequence outside the shape), and its text is appended to the prompt
    /// so the model is also told in plain terms what is expected of it. One drifting out of sync
    /// with the other is not possible because both come from the same generated text.
    /// </summary>
    public async Task<T> GenerateStructuredAsync<T>(string prompt, CancellationToken ct = default) where T : class
        => (await GenerateStructuredWithUsageAsync<T>(prompt, ct)).Result;

    /// <inheritdoc cref="CardiTrack.Application.Interfaces.Clients.IExternalAiClient.GenerateStructuredWithUsageAsync{T}"/>
    public async Task<CardiTrack.Application.DTOs.Common.AiGenerationResult<T>> GenerateStructuredWithUsageAsync<T>(
        string prompt, CancellationToken ct = default) where T : class
    {
        var schemaText = SchemaTextFor<T>();
        var fullPrompt = StructuredOutputSchema.PromptWithSchema(prompt, schemaText);
        var request = new OllamaGenerateRequest
        {
            Model = _settings.Model,
            Prompt = fullPrompt,
            Format = JsonNode.Parse(schemaText),
            Options = TokenBudget(),
        };
        LogClinicalText("generate_structured", "prompt (with reply schema appended)", fullPrompt);
        var (result, usage) = await SendInstrumentedCoreAsync<OllamaGenerateResponse, T>(
            operationName: "generate_structured",
            send: (client, token) => client.PostAsJsonAsync("/api/generate", request, token),
            selectContent: response => response.Response,
            parseContent: content => DeserializeStructured<T>(content, "generate_structured"),
            ct,
            requireCompleteContent: true);
        return new CardiTrack.Application.DTOs.Common.AiGenerationResult<T>(result, usage);
    }

    // Schema generation and the structured-output serializer options live in
    // StructuredOutputSchema, shared with VertexAiClient so the two providers cannot drift
    // apart on what a response type's wire shape is.
    internal static string SchemaTextFor<T>() => StructuredOutputSchema.TextFor<T>();

    /// <summary>
    /// Deserializes the model's structured reply into <typeparamref name="T"/>. Per the DPIA
    /// invariant this class opens with, a malformed reply must not leak into a log or exception —
    /// <see cref="JsonException"/>'s <c>Path</c>/<c>LineNumber</c>/<c>BytePositionInLine</c> are
    /// safe (they describe a location, not content); its <c>Message</c> is not read here for the
    /// same reason the outer envelope parse below avoids Newtonsoft's.
    /// </summary>
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
                "MedGemma {Operation} returned structured content that could not be parsed into "
                + "{Type}: error at '{Path}' (line {Line}, byte {BytePosition}); body length {BodyLength} chars",
                operationName, typeof(T).Name, ex.Path ?? "$", ex.LineNumber ?? 0, ex.BytePositionInLine ?? 0,
                content.Length);
            throw new HttpRequestException(
                $"MedGemma {operationName} returned structured content that could not be parsed into "
                + $"{typeof(T).Name} (error at '{ex.Path ?? "$"}', line {ex.LineNumber ?? 0}, "
                + $"byte {ex.BytePositionInLine ?? 0}).");
        }
    }

    /// <summary>
    /// The single instrumented path every operation goes through: one client span (the auto
    /// HttpClient span nests beneath it), the GenAI duration/token metrics, and a per-call
    /// log line. Span naming and attributes follow the OpenTelemetry GenAI semantic
    /// conventions. All Activity access is null-tolerant — with no APM engine there is no
    /// listener and <see cref="ActivitySource.StartActivity(string, ActivityKind)"/> returns null.
    /// Returns the parsed result alongside the usage <see cref="AiTelemetry"/> already derives from
    /// <c>meta</c>, so a caller that needs it (<see cref="GenerateWithUsageAsync"/>,
    /// <see cref="GenerateStructuredWithUsageAsync{T}"/>) reads the same numbers the telemetry does,
    /// rather than a second, possibly-drifting parse of the same response.
    /// </summary>
    /// <param name="allowEmptyContent">
    /// Whether an empty completion is the expected shape. Only <see cref="WarmUpAsync"/> sets it:
    /// everywhere else a model that answered with nothing is worth a warning.
    /// </param>
    /// <param name="maxAttempts">
    /// How many times to send before giving up. Defaults to <see cref="MaxAttempts"/>; only
    /// <see cref="WarmUpAsync"/> lowers it, for the reason given there.
    /// </param>
    /// <param name="requireCompleteContent">
    /// Whether a completion cut short at the token ceiling is a failure rather than a short
    /// answer. Set by <see cref="GenerateStructuredWithUsageAsync{T}"/> alone: prose that stops
    /// early is still prose and the caller can judge it, but JSON that stops early is not JSON,
    /// and letting it reach the deserializer turns a budget problem into a parse error pointing
    /// at whatever byte the cut happened to land on.
    /// </param>
    /// <summary>
    /// The inspection switch's one outlet. Writes <paramref name="text"/> verbatim — health data
    /// included, which is the point of the switch — under <see cref="ClinicalInspectionEvent"/>,
    /// and only when <see cref="IMedGemmaModelSettings.LogClinicalOutput"/> is on. The message
    /// names the model, the operation and which side of the exchange the text is, so a log
    /// scan can pair a completion with the prompt that produced it. Nothing else in this class
    /// may log prompt or completion text.
    /// </summary>
    private void LogClinicalText(string operationName, string kind, string text)
    {
        if (!_settings.LogClinicalOutput)
            return;

        _logger.LogInformation(
            ClinicalInspectionEvent,
            "MedGemma clinical inspection — {Model} {Operation} {Kind} ({Length} chars):\n{Text}",
            _settings.Model, operationName, kind, text.Length, text);
    }

    private async Task<(TResult Result, CardiTrack.Application.DTOs.Common.AiUsage Usage)> SendInstrumentedCoreAsync<TResponse, TResult>(
        string operationName,
        Func<HttpClient, CancellationToken, Task<HttpResponseMessage>> send,
        Func<TResponse, string?> selectContent,
        Func<string, TResult> parseContent,
        CancellationToken ct,
        bool allowEmptyContent = false,
        int maxAttempts = MaxAttempts,
        bool requireCompleteContent = false)
        where TResponse : OllamaResponseMetadata
    {
        using var activity = AiTelemetry.Source.StartActivity(
            $"{operationName} {_settings.Model}", ActivityKind.Client);
        activity?.SetTag(AiTelemetry.OperationNameTag, operationName);
        activity?.SetTag(AiTelemetry.ProviderNameTag, ProviderName);
        activity?.SetTag(AiTelemetry.SystemTag, ProviderName);
        activity?.SetTag(AiTelemetry.RequestModelTag, _settings.Model);

        var stopwatch = Stopwatch.StartNew();
        string? errorType = null;
        try
        {
            var client = _httpClientFactory.CreateClient(_httpClientName);
            var body = await SendWithRetryAsync(client, send, operationName, stopwatch, maxAttempts, ct);
            // Lenient parse on purpose: the strict JsonUtility.Deserialize throws a
            // JsonDeserializationException whose message embeds a 1000-char payload preview —
            // for MedGemma that preview is model output derived from health data, so it must
            // never be constructed here. Error paths and positions are safe to report;
            // Newtonsoft error *messages* can quote payload characters, so they are not.
            if (!JsonUtility.TryDeserialize<TResponse>(body, out var parsed, out var errors) || parsed is null)
            {
                errorType = "invalid_response";
                var first = errors.Count > 0 ? errors[0] : null;
                _logger.LogError(
                    "MedGemma {Operation} returned a response that could not be parsed: "
                    + "{ErrorCount} JSON error(s), first at '{Path}' (line {Line}, pos {Position}); "
                    + "body length {BodyLength} chars",
                    operationName, errors.Count, first?.Path is { Length: > 0 } path ? path : "$",
                    first?.LineNumber ?? 0, first?.LinePosition ?? 0, body.Length);
                throw new HttpRequestException(
                    $"MedGemma {operationName} returned a response that could not be parsed "
                    + $"({errors.Count} JSON error(s), first at '{(first?.Path is { Length: > 0 } p ? p : "$")}', "
                    + $"line {first?.LineNumber ?? 0}, pos {first?.LinePosition ?? 0}).");
            }

            OllamaResponseMetadata meta = parsed;
            var content = selectContent(parsed);
            if (content is not null)
                LogClinicalText(operationName, "completion", content);
            activity?.SetTag(AiTelemetry.ResponseModelTag, meta.Model);
            if (meta.PromptEvalCount is { } inputTokens)
            {
                activity?.SetTag(AiTelemetry.InputTokensTag, inputTokens);
                AiTelemetry.TokenUsage.Record(inputTokens, TokenTags(operationName, "input"));
            }
            if (meta.EvalCount is { } outputTokens)
            {
                activity?.SetTag(AiTelemetry.OutputTokensTag, outputTokens);
                AiTelemetry.TokenUsage.Record(outputTokens, TokenTags(operationName, "output"));
            }

            // Ollama reports "length" when generation stopped because the budget ran out rather
            // than because the model was finished. What follows is the same either way — the
            // reply is missing its tail — so this is decided here, once, before any caller's
            // parse gets to mistake a cut-off reply for a malformed one.
            if (string.Equals(meta.DoneReason, LengthDoneReason, StringComparison.Ordinal))
            {
                if (requireCompleteContent)
                {
                    errorType = "truncated";
                    _logger.LogError(
                        "MedGemma {Operation} stopped at the token budget rather than finishing "
                        + "(done_reason {DoneReason}): {OutputTokens} output token(s) against a "
                        + "{MaxOutputTokens} ceiling, {InputTokens} prompt token(s) in a "
                        + "{ContextTokens}-token window. The reply is incomplete. Raise "
                        + "MaxOutputTokens or ContextTokens for this model slot, or shorten the prompt.",
                        operationName, meta.DoneReason, meta.EvalCount, _settings.MaxOutputTokens,
                        meta.PromptEvalCount, _settings.ContextTokens);
                    throw new HttpRequestException(
                        $"MedGemma {operationName} stopped at the token budget rather than finishing "
                        + $"({meta.EvalCount} output token(s) against a {_settings.MaxOutputTokens} "
                        + $"ceiling in a {_settings.ContextTokens}-token window), so the reply is incomplete.");
                }

                // Free text: the caller gets what was produced, as before, but the cut is on the
                // record — otherwise a reply that stops mid-sentence is indistinguishable from
                // one the model chose to end there.
                _logger.LogWarning(
                    "MedGemma {Operation} stopped at the token budget rather than finishing "
                    + "(done_reason {DoneReason}): {OutputTokens} output token(s) against a "
                    + "{MaxOutputTokens} ceiling. The reply is cut short.",
                    operationName, meta.DoneReason, meta.EvalCount, _settings.MaxOutputTokens);
            }

            if (string.IsNullOrEmpty(content) && !allowEmptyContent)
            {
                _logger.LogWarning(
                    "MedGemma {Operation} returned empty content (done_reason {DoneReason})",
                    operationName, meta.DoneReason);
            }

            // Debug, not Information: model/tokens/elapsed are already on the span tags and
            // AiTelemetry's metrics set/recorded just above — this line's only unique value is
            // done_reason/server-side timing breakdown for local troubleshooting, not something
            // worth shipping on every single successful call.
            _logger.LogDebug(
                "MedGemma {Operation} completed: model {Model}, {ElapsedMs} ms, "
                + "tokens in {InputTokens} out {OutputTokens}, done_reason {DoneReason}, "
                + "server total {ServerTotalMs} ms (load {LoadMs}, prompt_eval {PromptEvalMs}, eval {EvalMs}), "
                + "trace {TraceId}",
                operationName, meta.Model ?? _settings.Model, stopwatch.ElapsedMilliseconds,
                meta.PromptEvalCount, meta.EvalCount, meta.DoneReason,
                NsToMs(meta.TotalDurationNs), NsToMs(meta.LoadDurationNs),
                NsToMs(meta.PromptEvalDurationNs), NsToMs(meta.EvalDurationNs),
                (activity ?? Activity.Current)?.TraceId.ToString());

            var usage = new CardiTrack.Application.DTOs.Common.AiUsage
            {
                ModelName = meta.Model ?? _settings.Model,
                InputTokens = meta.PromptEvalCount,
                OutputTokens = meta.EvalCount,
                DurationMs = NsToMs(meta.TotalDurationNs) ?? stopwatch.ElapsedMilliseconds,
            };
            return (parseContent(content ?? string.Empty), usage);
        }
        catch (Exception ex)
        {
            // No AddException/RecordException: exception messages can embed payload fragments,
            // and the type alone is what dashboards aggregate on. A status-carrying failure
            // (the retry loop exhausted on a non-success response) is tagged by that status,
            // same as before retries existed; anything else falls back to the exception type.
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
    /// Sends the request, retrying a non-success response up to <paramref name="maxAttempts"/>
    /// times with a backoff sized by what the rejection means — see <see cref="BackoffFor"/>. Only the
    /// HTTP outcome is retried — a 200 with an unparseable body is the model having already
    /// answered, not a transport blip, so that path still fails on the first attempt, and a
    /// client-side timeout is terminal for the reasons <see cref="SendOnceAsync"/> gives.
    /// Nothing is logged per attempt: a sustained outage already produces one error per call,
    /// and logging every retry too would triple that volume for no diagnostic gain. The final
    /// attempt's failure is logged and thrown exactly as the single attempt used to be; a call
    /// that only succeeds after a retry logs one warning noting that, since "it took N attempts"
    /// is worth knowing even though "it failed" is not, twice over.
    /// </summary>
    private async Task<string> SendWithRetryAsync(
        HttpClient client,
        Func<HttpClient, CancellationToken, Task<HttpResponseMessage>> send,
        string operationName,
        Stopwatch stopwatch,
        int maxAttempts,
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
                        "MedGemma {Operation} succeeded on attempt {Attempt} of {MaxAttempts} "
                        + "after a transient failure.",
                        operationName, attempt, maxAttempts);
                }
                return await response.Content.ReadAsStringAsync(ct);
            }

            var statusCode = response.StatusCode;
            if (attempt >= maxAttempts)
            {
                _logger.LogError(
                    "MedGemma {Operation} failed: HTTP {StatusCode} after {ElapsedMs} ms ({Attempts} attempt(s))",
                    operationName, (int)statusCode, stopwatch.ElapsedMilliseconds, attempt);
                throw new HttpRequestException(
                    $"MedGemma {operationName} returned HTTP {(int)statusCode}.",
                    inner: null, statusCode: statusCode);
            }

            var backoff = BackoffFor(statusCode, response.Headers.RetryAfter, attempt);

            // Released before the wait, not after it: a saturation backoff is measured in
            // tens of seconds, and holding the failed response holds its pooled connection
            // for all of them. Disposing twice (here and at scope exit) is a no-op.
            response.Dispose();
            await Task.Delay(backoff, _timeProvider, ct);
        }
    }

    /// <summary>
    /// One attempt, with the client-side timeout separated from the caller giving up. Both
    /// surface from <see cref="HttpClient"/> as <see cref="TaskCanceledException"/>, and left
    /// undistinguished the timeout reaches telemetry tagged <c>error.type</c>
    /// <c>System.Threading.Tasks.TaskCanceledException</c> — which reads as "something cancelled
    /// this" when what happened is that MedGemma took longer than
    /// <see cref="PrivateAiSettings.TimeoutSeconds"/> to answer. The distinction is the whole
    /// diagnosis: one is a caller's choice, the other is the model being too slow.
    /// </summary>
    /// <remarks>
    /// Terminal on purpose — a timeout is not retried. The request is most likely still queued
    /// or executing on the far side (Ollama serves inference from a queue and does not abandon a
    /// generation because the caller hung up), so re-sending adds load to the exact thing that
    /// was already too slow, and three attempts at
    /// <see cref="PrivateAiSettings.TimeoutSeconds"/> apiece would take the wall clock well past
    /// the schedule of the job that started them. Rate limiting is the case where waiting and
    /// re-asking helps; a timeout is the case where it does not.
    /// </remarks>
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
                "MedGemma {Operation} timed out after {ElapsedMs} ms "
                + "(HttpClient.Timeout is {TimeoutSeconds} s)",
                operationName, stopwatch.ElapsedMilliseconds, _settings.TimeoutSeconds);
            throw new TimeoutException(
                $"MedGemma {operationName} timed out after {_settings.TimeoutSeconds} s.", ex);
        }
    }

    /// <summary>
    /// How long to wait before the next attempt. A server that said <c>Retry-After</c> is
    /// answered on its own terms, capped by <see cref="MaxBackoff"/> so a header measured in
    /// hours cannot park a job indefinitely.
    /// </summary>
    /// <remarks>
    /// Absent that header the wait is sized by what the status means. 429 and 503 are
    /// saturation: on Cloud Run they mean every instance is busy and the queue is full, and what
    /// clears it is an in-flight inference finishing — tens of seconds on a CPU-served 4B model,
    /// so a two-second wait re-asks a queue that has not moved and spends the attempt for
    /// nothing. Everything else retried here is the cold-start/IAM-propagation case
    /// <see cref="MaxAttempts"/> describes, which does resolve in a couple of seconds, and
    /// stretching that wait would only delay the answer.
    /// </remarks>
    private TimeSpan BackoffFor(HttpStatusCode statusCode, RetryConditionHeaderValue? retryAfter, int attempt)
    {
        if (AdvisedDelay(retryAfter) is { } advised)
            return advised > MaxBackoff ? MaxBackoff : advised;

        var seconds = statusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable
            ? attempt * SaturationBackoffSeconds
            : attempt * ColdStartBackoffSeconds;
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

    /// <summary>
    /// The token budget every request carries. Read per call rather than cached so a settings
    /// object that can be re-read stays authoritative, the same way <c>Model</c> is.
    /// </summary>
    private OllamaOptions TokenBudget() => new()
    {
        NumCtx = _settings.ContextTokens,
        NumPredict = _settings.MaxOutputTokens,
    };

    private TagList TokenlessTags(string operationName) => new()
    {
        { AiTelemetry.OperationNameTag, operationName },
        { AiTelemetry.ProviderNameTag, ProviderName },
        { AiTelemetry.RequestModelTag, _settings.Model },
    };

    /// <summary>Ollama reports durations in nanoseconds.</summary>
    private static long? NsToMs(long? nanoseconds) =>
        nanoseconds is { } value ? value / 1_000_000 : null;

    // Requests serialize through PostAsJsonAsync = System.Text.Json, so request records use
    // [JsonPropertyName]. Responses deserialize through JsonUtility = Newtonsoft, so response
    // records must use [JsonProperty] — Newtonsoft matches case-insensitively but does not map
    // snake_case, and [JsonPropertyName] means nothing to it. Mixing them up fails silently.

    private record OllamaGenerateRequest
    {
        [JsonPropertyName("model")] public required string Model { get; init; }
        [JsonPropertyName("prompt")] public required string Prompt { get; init; }
        [JsonPropertyName("stream")] public bool Stream { get; init; } = false;

        /// <summary>The token budget — see <see cref="OllamaOptions"/>.</summary>
        [JsonPropertyName("options")] public required OllamaOptions Options { get; init; }

        /// <summary>
        /// Always off: on a thinking-family model (e.g. Qwen), Ollama's default routes the whole
        /// generation into a separate <c>thinking</c> field and leaves the content field this
        /// client reads (<c>response</c> here, <c>message.content</c> on the chat endpoint)
        /// empty, which surfaces as "returned structured content that could not be parsed"
        /// (measured against qwen3.5:4b, 2026-08-20). Non-thinking models (gemma3, gemma4)
        /// accept and ignore the flag.
        /// </summary>
        [JsonPropertyName("think")] public bool Think { get; init; } = false;

        /// <summary>A JSON Schema — Ollama's grammar-constrained decoding. Null for a free-text call.</summary>
        [JsonPropertyName("format")] public JsonNode? Format { get; init; }
    }

    private record OllamaChatRequest
    {
        [JsonPropertyName("model")] public required string Model { get; init; }
        [JsonPropertyName("messages")] public required List<OllamaMessage> Messages { get; init; }
        [JsonPropertyName("stream")] public bool Stream { get; init; } = false;

        /// <summary>Same reason as <see cref="OllamaGenerateRequest.Think"/> — both endpoints
        /// route a thinking model's output away from the field this client reads.</summary>
        [JsonPropertyName("think")] public bool Think { get; init; } = false;

        /// <summary>The token budget — see <see cref="OllamaOptions"/>.</summary>
        [JsonPropertyName("options")] public required OllamaOptions Options { get; init; }
    }

    /// <summary>
    /// Ollama's per-request model parameters. Only the two that decide whether a reply can finish
    /// are set: everything else (temperature, top_p, the sampler) belongs to the model tag's own
    /// Modelfile, where it is versioned with the weights rather than with this client.
    /// </summary>
    private record OllamaOptions
    {
        /// <summary>Prompt and completion share this window.</summary>
        [JsonPropertyName("num_ctx")] public required int NumCtx { get; init; }

        /// <summary>Ceiling on the completion alone.</summary>
        [JsonPropertyName("num_predict")] public required int NumPredict { get; init; }
    }

    private record OllamaMessage
    {
        [JsonPropertyName("role")] public required string Role { get; init; }
        [JsonPropertyName("content")] public required string Content { get; init; }
    }

    /// <summary>
    /// Metadata Ollama returns on every non-streaming completion. All nullable: older or
    /// load-failure responses omit fields, and telemetry must degrade to absent tags, not throw.
    /// </summary>
    internal abstract record OllamaResponseMetadata
    {
        [JsonProperty("model")] public string? Model { get; init; }
        [JsonProperty("done")] public bool? Done { get; init; }
        [JsonProperty("done_reason")] public string? DoneReason { get; init; }
        [JsonProperty("total_duration")] public long? TotalDurationNs { get; init; }
        [JsonProperty("load_duration")] public long? LoadDurationNs { get; init; }
        [JsonProperty("prompt_eval_count")] public int? PromptEvalCount { get; init; }
        [JsonProperty("prompt_eval_duration")] public long? PromptEvalDurationNs { get; init; }
        [JsonProperty("eval_count")] public int? EvalCount { get; init; }
        [JsonProperty("eval_duration")] public long? EvalDurationNs { get; init; }
    }

    private sealed record OllamaGenerateResponse : OllamaResponseMetadata
    {
        [JsonProperty("response")] public string? Response { get; init; }
    }

    private sealed record OllamaChatResponse : OllamaResponseMetadata
    {
        [JsonProperty("message")] public OllamaChatResponseMessage? Message { get; init; }
    }

    private sealed record OllamaChatResponseMessage
    {
        [JsonProperty("content")] public string? Content { get; init; }
    }
}
