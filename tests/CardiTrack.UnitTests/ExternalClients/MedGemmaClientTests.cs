using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CardiTrack.Application.DTOs.Common;
using CardiTrack.Infrastructure.ExternalClients.Medical;
using CardiTrack.Infrastructure.Services;
using CardiTrack.Infrastructure.Settings;
using CardiTrack.Shared.Telemetry;
using CardiTrack.UnitTests.Mobile;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace CardiTrack.UnitTests.ExternalClients;

/// <summary>
/// Pins the observability contract of the MedGemma path: every call emits a GenAI-semconv
/// span, duration/token metrics and a log line — and none of those signals may ever carry
/// prompt text or model output (the DPIA invariant). The content-leak tests are the point
/// of this file; the rest keeps the request/response shape honest.
/// </summary>
/// <remarks>
/// In the "AiTelemetry" collection with <see cref="VertexAiClientTests"/>: both suites listen to
/// the one shared ActivitySource/Meter, so running them in parallel makes each capture the
/// other's spans.
/// </remarks>
[Collection("AiTelemetry")]
public class MedGemmaClientTests
{
    private const string Model = "medgemma";
    private const string Prompt = "Weekly vitals prompt with MedicalNotes: chest pain at night";
    private const string ResponseText = "Trends look stable.";

    /// <summary>Realistic non-streaming /api/generate payload; durations are nanoseconds.</summary>
    private const string GeneratePayload =
        """
        {"model":"medgemma-4b","created_at":"2026-08-09T10:00:00Z","response":"Trends look stable.",
         "done":true,"done_reason":"stop","total_duration":45000000000,"load_duration":2000000000,
         "prompt_eval_count":412,"prompt_eval_duration":900000000,"eval_count":128,"eval_duration":42000000000}
        """;

    private const string ChatPayload =
        """
        {"model":"medgemma-4b","created_at":"2026-08-09T10:00:00Z",
         "message":{"role":"assistant","content":"Trends look stable."},
         "done":true,"done_reason":"stop","total_duration":45000000000,
         "prompt_eval_count":412,"eval_count":128}
        """;

    [Fact]
    public async Task GenerateAsync_PostsModelAndPromptToApiGenerate_WithoutStreaming()
    {
        var handler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, GeneratePayload);
        var client = CreateClient(handler, out _);

        await client.GenerateAsync(Prompt);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/generate", request.Uri?.AbsolutePath);
        Assert.Contains($"\"model\":\"{Model}\"", request.Body);
        Assert.Contains("\"stream\":false", request.Body);
        Assert.Contains("chest pain at night", request.Body);
    }

    [Fact]
    public async Task GenerateAsync_ReturnsTheResponseText()
    {
        var handler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, GeneratePayload);
        var client = CreateClient(handler, out _);

        Assert.Equal(ResponseText, await client.GenerateAsync(Prompt));
    }

    [Fact]
    public async Task GenerateAsync_EmitsAClientSpan_WithGenAiTags()
    {
        using var capture = new SpanCapture();
        var handler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, GeneratePayload);
        var client = CreateClient(handler, out _);

        await client.GenerateAsync(Prompt);

        var span = Assert.Single(capture.Stopped);
        Assert.Equal($"generate_content {Model}", span.DisplayName);
        Assert.Equal(ActivityKind.Client, span.Kind);
        Assert.NotEqual(ActivityStatusCode.Error, span.Status);
        Assert.Equal("generate_content", span.GetTagItem("gen_ai.operation.name"));
        Assert.Equal("ollama", span.GetTagItem("gen_ai.provider.name"));
        Assert.Equal("ollama", span.GetTagItem("gen_ai.system"));
        Assert.Equal(Model, span.GetTagItem("gen_ai.request.model"));
        Assert.Equal("medgemma-4b", span.GetTagItem("gen_ai.response.model"));
        Assert.Equal(412, span.GetTagItem("gen_ai.usage.input_tokens"));
        Assert.Equal(128, span.GetTagItem("gen_ai.usage.output_tokens"));
        Assert.Null(span.GetTagItem("error.type"));
    }

    /// <summary>
    /// The DPIA regression pin: prompts and completions are health data and must never
    /// appear on a span, whatever tags later changes add.
    /// </summary>
    [Fact]
    public async Task GenerateAsync_NeverPutsPromptOrResponseTextOnTheSpan()
    {
        using var capture = new SpanCapture();
        var handler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, GeneratePayload);
        var client = CreateClient(handler, out _);

        await client.GenerateAsync(Prompt);

        var span = Assert.Single(capture.Stopped);
        foreach (var (key, value) in span.TagObjects)
        {
            var text = value?.ToString() ?? string.Empty;
            Assert.DoesNotContain("chest pain", text);
            Assert.DoesNotContain(ResponseText, text);
        }
        Assert.Empty(span.Events);
    }

    [Fact]
    public async Task GenerateAsync_RecordsDurationAndTokenHistograms()
    {
        using var metrics = new MetricCapture();
        var handler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, GeneratePayload);
        var client = CreateClient(handler, out _);

        await client.GenerateAsync(Prompt);

        var duration = Assert.Single(metrics.Doubles, m => m.Instrument == "gen_ai.client.operation.duration");
        Assert.True(duration.Value > 0);
        Assert.Equal("generate_content", duration.Tags["gen_ai.operation.name"]);
        Assert.Equal(Model, duration.Tags["gen_ai.request.model"]);
        Assert.False(duration.Tags.ContainsKey("error.type"));

        var tokens = metrics.Longs.Where(m => m.Instrument == "gen_ai.client.token.usage").ToList();
        Assert.Equal(2, tokens.Count);
        Assert.Equal(412, Assert.Single(tokens, t => Equals(t.Tags["gen_ai.token.type"], "input")).Value);
        Assert.Equal(128, Assert.Single(tokens, t => Equals(t.Tags["gen_ai.token.type"], "output")).Value);
    }

    [Fact]
    public async Task GenerateAsync_TagsSpanAndDurationMetricWithErrorType_On500()
    {
        using var capture = new SpanCapture();
        using var metrics = new MetricCapture();
        var handler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.InternalServerError, "upstream error body");
        var client = CreateClient(handler, out var logger);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GenerateAsync(Prompt));

        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
        // The handler repeats its last enqueued response, so every retry attempt also sees 500 —
        // this is the exhausted-retries path, one request per attempt.
        Assert.Equal(3, handler.Requests.Count);
        var span = Assert.Single(capture.Stopped);
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal("500", span.GetTagItem("error.type"));

        var duration = Assert.Single(metrics.Doubles, m => m.Instrument == "gen_ai.client.operation.duration");
        Assert.Equal("500", duration.Tags["error.type"]);
        Assert.DoesNotContain(metrics.Longs, m => m.Instrument == "gen_ai.client.token.usage");

        var error = Assert.Single(logger.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains("500", error.Message);
        Assert.DoesNotContain("upstream error body", error.Message);
        // The exhausted-retry path logs exactly what the single-attempt path used to: one error,
        // nothing per attempt. A sustained outage must not triple its own log volume.
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    /// <summary>
    /// The Cloud Run cold-start / IAM-propagation case this retry exists for: a couple of
    /// platform-level rejections followed by a real answer, all within one logical call.
    /// </summary>
    [Fact]
    public async Task GenerateAsync_RetriesTransientFailures_AndReturnsTheEventualSuccess()
    {
        using var capture = new SpanCapture();
        var handler = new FakeHttpMessageHandler()
            .Enqueue(HttpStatusCode.NotFound, "")
            .Enqueue(HttpStatusCode.Forbidden, "")
            .Enqueue(HttpStatusCode.OK, GeneratePayload);
        var client = CreateClient(handler, out var logger);

        var result = await client.GenerateAsync(Prompt);

        Assert.Equal(ResponseText, result);
        Assert.Equal(3, handler.Requests.Count);
        var span = Assert.Single(capture.Stopped);
        Assert.NotEqual(ActivityStatusCode.Error, span.Status);
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Error);
        // One note that it took retries to succeed — not one per failed attempt.
        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("3", warning.Message);
    }

    /// <summary>
    /// A cold start clears in a couple of seconds, so the wait after one stays short. Nothing
    /// about the backoff for that case changes because the rate-limit case now waits longer.
    /// </summary>
    [Fact]
    public async Task GenerateAsync_KeepsTheShortBackoff_ForColdStartRejections()
    {
        var handler = new FakeHttpMessageHandler()
            .Enqueue(HttpStatusCode.NotFound, "")
            .Enqueue(HttpStatusCode.OK, GeneratePayload);
        var client = CreateClient(handler, out _, out var time);

        await client.GenerateAsync(Prompt);

        Assert.Equal(new[] { TimeSpan.FromSeconds(2) }, time.Delays);
    }

    /// <summary>
    /// 429 is not a cold start: on Cloud Run it means every instance is busy and the queue is
    /// full, and what frees one is an in-flight inference finishing — tens of seconds on a
    /// CPU-served 4B model. Re-asking two seconds later queries a queue that has not moved and
    /// spends the attempt for nothing, which is how a rate-limited digest burned all three
    /// attempts inside six seconds and failed the member anyway.
    /// </summary>
    [Fact]
    public async Task GenerateAsync_BacksOffLongerForRateLimiting_ThanForAColdStart()
    {
        var handler = new FakeHttpMessageHandler()
            .Enqueue(HttpStatusCode.TooManyRequests, "")
            .Enqueue(HttpStatusCode.TooManyRequests, "")
            .Enqueue(HttpStatusCode.OK, GeneratePayload);
        var client = CreateClient(handler, out _, out var time);

        Assert.Equal(ResponseText, await client.GenerateAsync(Prompt));
        Assert.Equal(new[] { TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30) }, time.Delays);
    }

    /// <summary>A server that says when to come back knows better than any step this client picks.</summary>
    [Fact]
    public async Task GenerateAsync_WaitsWhatTheServerAsksFor_WhenItSendsRetryAfter()
    {
        var handler = new FakeHttpMessageHandler()
            .Enqueue(RetryAfter(HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(7)))
            .Enqueue(HttpStatusCode.OK, GeneratePayload);
        var client = CreateClient(handler, out _, out var time);

        await client.GenerateAsync(Prompt);

        Assert.Equal(new[] { TimeSpan.FromSeconds(7) }, time.Delays);
    }

    /// <summary>
    /// Knowing better has a limit: a job that waits out an hour-long Retry-After has missed the
    /// schedule that started it and every one after.
    /// </summary>
    [Fact]
    public async Task GenerateAsync_CapsARetryAfterThatWouldParkTheJob()
    {
        var handler = new FakeHttpMessageHandler()
            .Enqueue(RetryAfter(HttpStatusCode.ServiceUnavailable, TimeSpan.FromHours(1)))
            .Enqueue(HttpStatusCode.OK, GeneratePayload);
        var client = CreateClient(handler, out _, out var time);

        await client.GenerateAsync(Prompt);

        Assert.Equal(new[] { TimeSpan.FromSeconds(60) }, time.Delays);
    }

    /// <summary>
    /// <see cref="HttpClient"/> reports its own timeout as a <see cref="TaskCanceledException"/>,
    /// which is also what a caller giving up produces — so an inference that overran 300 s
    /// reached the error dashboards tagged
    /// <c>error.type: System.Threading.Tasks.TaskCanceledException</c>, reading as "something
    /// cancelled this" rather than "the model was too slow".
    /// </summary>
    [Fact]
    public async Task GenerateAsync_ReportsAClientTimeoutAsOne_AndDoesNotRetryIt()
    {
        using var capture = new SpanCapture();
        var handler = new FakeHttpMessageHandler().Throws(new TaskCanceledException(
            "The request was canceled due to the configured HttpClient.Timeout of 300 seconds elapsing.",
            new TimeoutException()));
        var client = CreateClient(handler, out var logger);

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => client.GenerateAsync(Prompt));

        Assert.Contains("300", ex.Message);
        // Terminal on purpose: the far side is most likely still generating the answer that was
        // already too slow, so a second ask only adds to what it is behind on.
        Assert.Single(handler.Requests);
        var span = Assert.Single(capture.Stopped);
        Assert.Equal("System.TimeoutException", span.GetTagItem("error.type"));
        var error = Assert.Single(logger.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains("timed out", error.Message);
    }

    /// <summary>The other half of that distinction: a caller who cancels gets a cancellation.</summary>
    [Fact]
    public async Task GenerateAsync_LeavesTheCallersOwnCancellationAsCancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var handler = new FakeHttpMessageHandler().Throws(new TaskCanceledException());
        var client = CreateClient(handler, out _);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GenerateAsync(Prompt, cts.Token));
    }

    private static Func<HttpRequestMessage, HttpResponseMessage> RetryAfter(
        HttpStatusCode status, TimeSpan delta) =>
        _ =>
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent("") };
            response.Headers.RetryAfter = new RetryConditionHeaderValue(delta);
            return response;
        };

    /// <summary>
    /// A malformed body is model output (health data): the strict JsonUtility.Deserialize
    /// would embed a 1000-char payload preview in the exception message, so the client must
    /// take the lenient path and report positions only.
    /// </summary>
    [Fact]
    public async Task GenerateAsync_Throws_WithoutLeakingTheBody_WhenJsonIsMalformed()
    {
        using var capture = new SpanCapture();
        var handler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, "{\"response\":\"PATIENT-SECRET\"");
        var client = CreateClient(handler, out var logger);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GenerateAsync(Prompt));

        Assert.DoesNotContain("PATIENT-SECRET", ex.Message);
        Assert.All(logger.Entries, e => Assert.DoesNotContain("PATIENT-SECRET", e.Message));
        var span = Assert.Single(capture.Stopped);
        Assert.Equal("invalid_response", span.GetTagItem("error.type"));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task GenerateAsync_LogsAWarning_AndReturnsEmpty_WhenContentIsMissing()
    {
        var handler = new FakeHttpMessageHandler()
            .Enqueue(HttpStatusCode.OK, "{\"model\":\"medgemma-4b\",\"done\":true,\"done_reason\":\"stop\"}");
        var client = CreateClient(handler, out var logger);

        var result = await client.GenerateAsync(Prompt);

        Assert.Equal(string.Empty, result);
        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("stop", warning.Message);
    }

    [Fact]
    public async Task GenerateAsync_LogsCompletionAtInformation_WithTokenCountsAndNoPromptText()
    {
        var handler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, GeneratePayload);
        var client = CreateClient(handler, out var logger);

        await client.GenerateAsync(Prompt);

        var completion = Assert.Single(logger.Entries, e => e.Level == LogLevel.Information);
        Assert.Contains("412", completion.Message);
        Assert.Contains("128", completion.Message);
        Assert.All(logger.Entries, e =>
        {
            Assert.DoesNotContain("chest pain", e.Message);
            Assert.DoesNotContain(ResponseText, e.Message);
        });
    }

    [Fact]
    public async Task ChatAsync_PostsHistoryThenUserMessage_ToApiChat()
    {
        var handler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, ChatPayload);
        var client = CreateClient(handler, out _);
        var history = new List<ChatMessage>
        {
            new() { Role = ChatRole.User, Content = "Hi" },
            new() { Role = ChatRole.Model, Content = "Hello" },
        };

        var result = await client.ChatAsync(history, "How were the last two nights?");

        Assert.Equal(ResponseText, result);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("/api/chat", request.Uri?.AbsolutePath);
        Assert.Contains("\"stream\":false", request.Body);
        var body = request.Body!;
        Assert.True(body.IndexOf("Hi", StringComparison.Ordinal)
            < body.IndexOf("Hello", StringComparison.Ordinal));
        Assert.True(body.IndexOf("Hello", StringComparison.Ordinal)
            < body.IndexOf("How were the last two nights?", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ChatAsync_EmitsAChatSpan_WithTokenTags()
    {
        using var capture = new SpanCapture();
        var handler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, ChatPayload);
        var client = CreateClient(handler, out _);

        await client.ChatAsync([], "How were the last two nights?");

        var span = Assert.Single(capture.Stopped);
        Assert.Equal($"chat {Model}", span.DisplayName);
        Assert.Equal("chat", span.GetTagItem("gen_ai.operation.name"));
        Assert.Equal(412, span.GetTagItem("gen_ai.usage.input_tokens"));
        Assert.Equal(128, span.GetTagItem("gen_ai.usage.output_tokens"));
    }

    // ── GenerateStructuredAsync ─────────────────────────────────────────────────

    private sealed record TestStructuredResponse
    {
        public required string Summary { get; init; }
    }

    private sealed record TestDescribedResponse
    {
        [Description("Two sentences on the trend. Not a restatement of these instructions.")]
        public required string Summary { get; init; }
    }

    /// <summary>Wraps a model reply (itself JSON) inside the Ollama envelope, matching how a real
    /// structured-output call actually arrives: <c>response</c> is a JSON *string*.</summary>
    private static string StructuredPayload(string modelReplyJson) =>
        $$"""
        {"model":"medgemma-4b","created_at":"2026-08-09T10:00:00Z",
         "response":{{JsonSerializer.Serialize(modelReplyJson)}},
         "done":true,"done_reason":"stop","total_duration":45000000000,
         "prompt_eval_count":412,"eval_count":128}
        """;

    [Fact]
    public async Task GenerateStructuredAsync_SetsTheSchemaAsFormat_AndDeserializesTheReply()
    {
        var handler = new FakeHttpMessageHandler()
            .Enqueue(HttpStatusCode.OK, StructuredPayload("""{"summary":"Trends look stable."}"""));
        var client = CreateClient(handler, out _);

        var result = await client.GenerateStructuredAsync<TestStructuredResponse>(Prompt);

        Assert.Equal("Trends look stable.", result.Summary);
        var request = Assert.Single(handler.Requests);
        var body = request.Body!;
        // The schema goes out twice: as the machine-enforced "format" field, and — per the strict
        // output instructions — spelled out in the prompt text itself, from the same generated text.
        Assert.Contains("\"format\":{", body);
        Assert.Contains("\"summary\":{\"type\":\"string\"}", body);
        Assert.Contains("Respond with ONLY a single JSON object", body);
        Assert.Contains(Prompt, body);
    }

    /// <summary>
    /// The schema is the only thing in the prompt that names the fields, so a field's description
    /// has to travel with it — otherwise the model is told a field is called "summary" and left to
    /// guess what a summary of anything would be, which is how the digest ended up echoing its own
    /// brief onto the Member Detail screen.
    /// </summary>
    [Fact]
    public async Task GenerateStructuredAsync_CarriesPropertyDescriptionsIntoTheSchema()
    {
        var handler = new FakeHttpMessageHandler()
            .Enqueue(HttpStatusCode.OK, StructuredPayload("""{"summary":"Trends look stable."}"""));
        var client = CreateClient(handler, out _);

        await client.GenerateStructuredAsync<TestDescribedResponse>(Prompt);

        var body = Assert.Single(handler.Requests).Body!;
        // Once inside "format" (grammar-constrained decoding) and once in the prompt text.
        Assert.Equal(2, CountOccurrences(body, "Two sentences on the trend."));
    }

    /// <summary>
    /// The schema is Ollama's grammar constraint, so what it marks optional the model may decline
    /// to write, and the order it declares is the order the model writes in. Both halves of that
    /// cost the digest its headline: declared first and optional, it was skipped on every one of
    /// 214 consecutive generations across 25 builds — always logged "the model returned none",
    /// never a length or echo rejection — while the equally optional suggestion and urgency
    /// fields, judged after the summary, arrived every time.
    /// </summary>
    /// <remarks>
    /// Every unit test of the digest stubs the reply with a headline already in it, so nothing in
    /// the suite could see this. Asserting on the generated schema is what closes that gap.
    /// </remarks>
    [Fact]
    public async Task GenerateStructuredAsync_RequiresTheDigestHeadline_AndAsksForItAfterTheSummary()
    {
        var handler = new FakeHttpMessageHandler().Enqueue(
            HttpStatusCode.OK,
            StructuredPayload("""{"summary":"Trends look stable.","headline":"A settled night"}"""));
        var client = CreateClient(handler, out _);

        await client.GenerateStructuredAsync<DigestGenerationService.DigestAiResponse>(Prompt);

        using var doc = JsonDocument.Parse(Assert.Single(handler.Requests).Body!);
        var schema = doc.RootElement.GetProperty("format");

        var required = schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("headline", required);

        // Immediately after the summary: the label describes prose the model has already written.
        var properties = schema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToList();
        Assert.Equal(properties.IndexOf("summary") + 1, properties.IndexOf("headline"));

        // A nullable type would let the grammar satisfy "required" with a null and put us straight
        // back to an empty headline, so the type has to be the bare string, not ["string","null"].
        Assert.Equal(
            JsonValueKind.String,
            schema.GetProperty("properties").GetProperty("headline").GetProperty("type").ValueKind);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    [Fact]
    public async Task GenerateStructuredAsync_PostsToApiGenerate_WithoutStreaming()
    {
        var handler = new FakeHttpMessageHandler()
            .Enqueue(HttpStatusCode.OK, StructuredPayload("""{"summary":"Trends look stable."}"""));
        var client = CreateClient(handler, out _);

        await client.GenerateStructuredAsync<TestStructuredResponse>(Prompt);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("/api/generate", request.Uri?.AbsolutePath);
        Assert.Contains("\"stream\":false", request.Body);
    }

    /// <summary>
    /// The DPIA invariant applies just as much to structured content as to free text: a malformed
    /// reply must not leak into a log or exception, even though it now fails at a different layer
    /// (deserializing the model's JSON into <c>T</c>, not parsing Ollama's envelope).
    /// </summary>
    [Fact]
    public async Task GenerateStructuredAsync_Throws_WithoutLeakingTheReply_WhenItDoesNotMatchTheSchema()
    {
        var handler = new FakeHttpMessageHandler()
            .Enqueue(HttpStatusCode.OK, StructuredPayload("PATIENT-SECRET not valid json"));
        var client = CreateClient(handler, out var logger);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GenerateStructuredAsync<TestStructuredResponse>(Prompt));

        Assert.DoesNotContain("PATIENT-SECRET", ex.Message);
        Assert.All(logger.Entries, e => Assert.DoesNotContain("PATIENT-SECRET", e.Message));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task GenerateStructuredAsync_NeverPutsPromptOrReplyTextOnTheSpan()
    {
        using var capture = new SpanCapture();
        var handler = new FakeHttpMessageHandler()
            .Enqueue(HttpStatusCode.OK, StructuredPayload("""{"summary":"Trends look stable."}"""));
        var client = CreateClient(handler, out _);

        await client.GenerateStructuredAsync<TestStructuredResponse>(Prompt);

        var span = Assert.Single(capture.Stopped);
        Assert.Equal("generate_structured", span.GetTagItem("gen_ai.operation.name"));
        foreach (var (_, value) in span.TagObjects)
        {
            var text = value?.ToString() ?? string.Empty;
            Assert.DoesNotContain("chest pain", text);
            Assert.DoesNotContain("Trends look stable.", text);
        }
    }

    private static MedGemmaClient CreateClient(FakeHttpMessageHandler handler, out ListLogger logger) =>
        CreateClient(handler, out logger, out _);

    private static MedGemmaClient CreateClient(
        FakeHttpMessageHandler handler, out ListLogger logger, out InstantRetryTimeProvider time)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("PrivateAiClient").Returns(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434") });
        var settings = new PrivateAiSettings { Model = Model, BaseUrl = "http://localhost:11434", TimeoutSeconds = 300 };
        logger = new ListLogger();
        time = new InstantRetryTimeProvider();
        return new MedGemmaClient(factory, settings, "PrivateAiClient", logger, time);
    }

    /// <summary>Real <see cref="TimeProvider"/> for everything except the retry backoff, which
    /// is recorded and then resolves immediately so a test exercising all attempts stays fast.
    /// Recording it is what lets a test assert the wait a status earns without serving it.</summary>
    private sealed class InstantRetryTimeProvider : TimeProvider
    {
        public List<TimeSpan> Delays { get; } = new();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (Delays) Delays.Add(dueTime);
            return base.CreateTimer(callback, state, TimeSpan.Zero, period);
        }
    }

    /// <summary>Captures completed activities from the CardiTrack.Ai source only.</summary>
    private sealed class SpanCapture : IDisposable
    {
        private readonly ActivityListener _listener;

        public List<Activity> Stopped { get; } = new();

        public SpanCapture()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == TelemetryNames.AiSource,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity => { lock (Stopped) Stopped.Add(activity); },
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public void Dispose() => _listener.Dispose();
    }

    /// <summary>Captures measurements from the CardiTrack.Ai meter only (BCL MeterListener).</summary>
    private sealed class MetricCapture : IDisposable
    {
        private readonly MeterListener _listener = new();

        public List<(string Instrument, double Value, Dictionary<string, object?> Tags)> Doubles { get; } = new();
        public List<(string Instrument, long Value, Dictionary<string, object?> Tags)> Longs { get; } = new();

        public MetricCapture()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == TelemetryNames.AiSource)
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            {
                lock (Doubles) Doubles.Add((instrument.Name, value, ToDictionary(tags)));
            });
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            {
                lock (Longs) Longs.Add((instrument.Name, value, ToDictionary(tags)));
            });
            _listener.Start();
        }

        public void Dispose() => _listener.Dispose();

        private static Dictionary<string, object?> ToDictionary(ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var dictionary = new Dictionary<string, object?>();
            foreach (var tag in tags)
                dictionary[tag.Key] = tag.Value;
            return dictionary;
        }
    }

    /// <summary>Hand-rolled recording logger, matching the suite's no-mocking-library style.</summary>
    private sealed class ListLogger : ILogger<MedGemmaClient>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Entries) Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
