using System.Diagnostics;
using System.Net.Http.Json;
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
/// </summary>
public class MedGemmaClient : IExternalAiClient
{
    private const string ProviderName = "ollama";

    /// <summary>
    /// Attempts per logical call, including the first. Covers a Cloud Run instance still
    /// spinning up or an IAM binding still propagating after a redeploy — both resolve within
    /// seconds. It will not mask a sustained outage: a misconfiguration that fails every
    /// attempt still surfaces as an error, just a few seconds later than today.
    /// </summary>
    private const int MaxAttempts = 3;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly PrivateAiSettings _settings;
    private readonly string _httpClientName;
    private readonly ILogger<MedGemmaClient> _logger;
    private readonly TimeProvider _timeProvider;

    public MedGemmaClient(
        IHttpClientFactory httpClientFactory,
        PrivateAiSettings settings,
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

    public Task<string> GenerateAsync(string prompt, CancellationToken ct = default)
    {
        var request = new OllamaGenerateRequest { Model = _settings.Model, Prompt = prompt };
        return SendInstrumentedAsync<OllamaGenerateResponse>(
            operationName: "generate_content",
            send: (client, token) => client.PostAsJsonAsync("/api/generate", request, token),
            selectContent: response => response.Response,
            ct);
    }

    public Task<string> ChatAsync(IReadOnlyList<ChatMessage> history, string userMessage, CancellationToken ct = default)
    {
        var messages = history
            .Select(m => new OllamaMessage { Role = m.Role == ChatRole.User ? "user" : "assistant", Content = m.Content })
            .Append(new OllamaMessage { Role = "user", Content = userMessage })
            .ToList();
        var request = new OllamaChatRequest { Model = _settings.Model, Messages = messages };
        return SendInstrumentedAsync<OllamaChatResponse>(
            operationName: "chat",
            send: (client, token) => client.PostAsJsonAsync("/api/chat", request, token),
            selectContent: response => response.Message?.Content,
            ct);
    }

    /// <summary>
    /// The single instrumented path both operations go through: one client span (the auto
    /// HttpClient span nests beneath it), the GenAI duration/token metrics, and a per-call
    /// log line. Span naming and attributes follow the OpenTelemetry GenAI semantic
    /// conventions. All Activity access is null-tolerant — with no APM engine there is no
    /// listener and <see cref="ActivitySource.StartActivity(string, ActivityKind)"/> returns null.
    /// </summary>
    private async Task<string> SendInstrumentedAsync<TResponse>(
        string operationName,
        Func<HttpClient, CancellationToken, Task<HttpResponseMessage>> send,
        Func<TResponse, string?> selectContent,
        CancellationToken ct)
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
            var body = await SendWithRetryAsync(client, send, operationName, stopwatch, ct);
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

            if (string.IsNullOrEmpty(content))
            {
                _logger.LogWarning(
                    "MedGemma {Operation} returned empty content (done_reason {DoneReason})",
                    operationName, meta.DoneReason);
            }

            _logger.LogInformation(
                "MedGemma {Operation} completed: model {Model}, {ElapsedMs} ms, "
                + "tokens in {InputTokens} out {OutputTokens}, done_reason {DoneReason}, "
                + "server total {ServerTotalMs} ms (load {LoadMs}, prompt_eval {PromptEvalMs}, eval {EvalMs}), "
                + "trace {TraceId}",
                operationName, meta.Model ?? _settings.Model, stopwatch.ElapsedMilliseconds,
                meta.PromptEvalCount, meta.EvalCount, meta.DoneReason,
                NsToMs(meta.TotalDurationNs), NsToMs(meta.LoadDurationNs),
                NsToMs(meta.PromptEvalDurationNs), NsToMs(meta.EvalDurationNs),
                (activity ?? Activity.Current)?.TraceId.ToString());

            return content ?? string.Empty;
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
    /// Sends the request, retrying a non-success response up to <see cref="MaxAttempts"/> times
    /// with a short backoff. Only the HTTP outcome is retried — a 200 with an unparseable body
    /// is the model having already answered, not a transport blip, so that path still fails on
    /// the first attempt. The final attempt's failure is logged and thrown exactly as the single
    /// attempt used to be, so callers and telemetry see the same shape as before retries existed.
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
            using var response = await send(client, ct);
            if (response.IsSuccessStatusCode)
                return await response.Content.ReadAsStringAsync(ct);

            var statusCode = response.StatusCode;
            if (attempt >= MaxAttempts)
            {
                _logger.LogError(
                    "MedGemma {Operation} failed: HTTP {StatusCode} after {ElapsedMs} ms ({Attempts} attempt(s))",
                    operationName, (int)statusCode, stopwatch.ElapsedMilliseconds, attempt);
                throw new HttpRequestException(
                    $"MedGemma {operationName} returned HTTP {(int)statusCode}.",
                    inner: null, statusCode: statusCode);
            }

            var delay = TimeSpan.FromSeconds(attempt * 2);
            _logger.LogWarning(
                "MedGemma {Operation} attempt {Attempt} failed: HTTP {StatusCode}, retrying in {DelayMs} ms",
                operationName, attempt, (int)statusCode, delay.TotalMilliseconds);
            await Task.Delay(delay, _timeProvider, ct);
        }
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
    }

    private record OllamaChatRequest
    {
        [JsonPropertyName("model")] public required string Model { get; init; }
        [JsonPropertyName("messages")] public required List<OllamaMessage> Messages { get; init; }
        [JsonPropertyName("stream")] public bool Stream { get; init; } = false;
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
