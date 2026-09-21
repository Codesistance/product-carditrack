using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using CardiTrack.Application.Exceptions;
using CardiTrack.Infrastructure.ExternalClients.Medical;
using CardiTrack.Infrastructure.ExternalClients.Vertex;
using CardiTrack.Shared.Telemetry;
using CardiTrack.UnitTests.Mobile;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace CardiTrack.UnitTests.ExternalClients;

/// <summary>
/// Pins the observability and privacy contract of the Vertex path to the same bar as
/// <see cref="MedGemmaClientTests"/>: every call emits a GenAI-semconv span, duration/token
/// metrics and a log line — and none of those signals may ever carry prompt text or model
/// output (the DPIA invariant; the rewrite slot sends caregiver questions through this client).
/// The content-leak tests are the point of this file, including the Vertex-specific outcome a
/// self-hosted model never produces: a platform-blocked response.
/// </summary>
/// <remarks>
/// In the "AiTelemetry" collection with <see cref="MedGemmaClientTests"/>: both suites listen to
/// the one shared ActivitySource/Meter, so running them in parallel makes each capture the
/// other's spans.
/// </remarks>
[Collection("AiTelemetry")]
public class VertexAiClientTests
{
    private const string Model = "gemini-2.5-flash-lite";
    private const string Prompt = "Caregiver question with health context: chest pain at night";
    private const string ResponseText = "Trends look stable.";

    /// <summary>
    /// Wall clock the first attempt is made to spend before it times out, so that the per-attempt
    /// and whole-call elapsed figures in the timeout log are far enough apart to tell apart. Long
    /// enough to survive timer granularity, short enough not to slow the suite.
    /// </summary>
    private static readonly TimeSpan FirstAttemptDuration = TimeSpan.FromMilliseconds(250);

    private const string ExpectedPath =
        "/v1/projects/test-project/locations/europe-west2/publishers/google/models/gemini-2.5-flash-lite:generateContent";

    private const string GeneratePayload =
        """
        {"candidates":[{"content":{"role":"model","parts":[{"text":"Trends look stable."}]},"finishReason":"STOP"}],
         "usageMetadata":{"promptTokenCount":412,"candidatesTokenCount":120,"thoughtsTokenCount":8,"totalTokenCount":540},
         "modelVersion":"gemini-2.5-flash-lite-001"}
        """;

    /// <summary>
    /// A structured reply that ran to the ceiling and stopped mid-string, with the counts the
    /// live failures carried: 8100 candidate + 76 thought tokens against 8192.
    /// </summary>
    private const string TruncatedStructuredPayload =
        """
        {"candidates":[{"content":{"role":"model","parts":[{"text":"{\"sources\":[\"Digest\"],\"note\":\"chest pain at nig"}]},
         "finishReason":"MAX_TOKENS"}],
         "usageMetadata":{"promptTokenCount":1770,"candidatesTokenCount":8100,"thoughtsTokenCount":76},
         "modelVersion":"gemini-2.5-flash-lite-001"}
        """;

    [Fact]
    public async Task GenerateAsync_PostsToTheRegionalGenerateContentEndpoint()
    {
        var handler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, GeneratePayload);
        var client = CreateClient(handler, out _);

        await client.GenerateAsync(Prompt);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("europe-west2-aiplatform.googleapis.com", request.Uri?.Host);
        Assert.Equal(ExpectedPath, request.Uri?.AbsolutePath);
        Assert.Contains("chest pain at night", request.Body);
        Assert.Contains("\"maxOutputTokens\":8192", request.Body);
        // Thinking off, always — the callers want the answer, not a reasoning trace.
        Assert.Contains("\"thinkingBudget\":0", request.Body);
        // Safety intervenes on high-probability harm only: clinical language trips the default.
        Assert.Contains("\"threshold\":\"BLOCK_ONLY_HIGH\"", request.Body);
        // A free-text call carries no structured-output keys at all.
        Assert.DoesNotContain("responseJsonSchema", request.Body);
        Assert.DoesNotContain("responseMimeType", request.Body);
    }

    [Fact]
    public async Task GenerateAsync_ReturnsTheResponseText()
    {
        var handler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, GeneratePayload);
        var client = CreateClient(handler, out _);

        Assert.Equal(ResponseText, await client.GenerateAsync(Prompt));
    }

    [Fact]
    public async Task GenerateWithUsageAsync_MapsUsageMetadata_FoldingThoughtsIntoOutput()
    {
        var handler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, GeneratePayload);
        var client = CreateClient(handler, out _);

        var result = await client.GenerateWithUsageAsync(Prompt);

        Assert.Equal(ResponseText, result.Result);
        Assert.Equal("gemini-2.5-flash-lite-001", result.Usage.ModelName);
        Assert.Equal(412, result.Usage.InputTokens);
        // 120 candidate tokens + 8 thought tokens: thoughts are generated output and billed as
        // such, so the persisted per-turn usage must not undercount them.
        Assert.Equal(128, result.Usage.OutputTokens);
        Assert.NotNull(result.Usage.DurationMs);
    }

    [Fact]
    public async Task ChatAsync_SendsHistoryInOrder_WithModelRoleForAssistantTurns()
    {
        var handler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, GeneratePayload);
        var client = CreateClient(handler, out _);

        await client.ChatAsync(
            [
                new CardiTrack.Application.DTOs.Common.ChatMessage
                    { Role = CardiTrack.Application.DTOs.Common.ChatRole.User, Content = "first question" },
                new CardiTrack.Application.DTOs.Common.ChatMessage
                    { Role = CardiTrack.Application.DTOs.Common.ChatRole.Model, Content = "first answer" },
            ],
            "second question");

        var request = Assert.Single(handler.Requests);
        var body = request.Body!;
        Assert.Contains("\"role\":\"model\"", body);
        Assert.True(body.IndexOf("first question", StringComparison.Ordinal)
            < body.IndexOf("first answer", StringComparison.Ordinal));
        Assert.True(body.IndexOf("first answer", StringComparison.Ordinal)
            < body.IndexOf("second question", StringComparison.Ordinal));
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
        Assert.Equal("gcp.vertex_ai", span.GetTagItem("gen_ai.provider.name"));
        Assert.Equal("gcp.vertex_ai", span.GetTagItem("gen_ai.system"));
        Assert.Equal(Model, span.GetTagItem("gen_ai.request.model"));
        Assert.Equal("gemini-2.5-flash-lite-001", span.GetTagItem("gen_ai.response.model"));
        Assert.Equal(412, span.GetTagItem("gen_ai.usage.input_tokens"));
        Assert.Equal(128, span.GetTagItem("gen_ai.usage.output_tokens"));
        Assert.Null(span.GetTagItem("error.type"));
    }

    /// <summary>
    /// The inspection switch, which is the one exception to everything this suite otherwise pins.
    /// With it on, the prompt and the completion are written verbatim under the shared
    /// <c>ClinicalInspection</c> event id — the same one the Ollama client uses, so one filter
    /// finds both halves of a two-slot generation and one filter drops them.
    /// </summary>
    [Fact]
    public async Task GenerateAsync_WritesThePromptAndCompletion_WhenInspectionIsOn()
    {
        var handler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, GeneratePayload);
        var client = CreateClient(handler, out var logger, out _, logClinicalOutput: true);

        await client.GenerateAsync(Prompt);

        var inspection = logger.Events
            .Where(e => e.Event == MedGemmaClient.ClinicalInspectionEvent)
            .ToList();

        Assert.Equal(2, inspection.Count);
        Assert.Contains(inspection, e => e.Message.Contains("prompt") && e.Message.Contains("chest pain at night"));
        Assert.Contains(inspection, e => e.Message.Contains("completion") && e.Message.Contains(ResponseText));
        Assert.All(inspection, e => Assert.Equal(4200, e.Event.Id));
    }

    /// <summary>
    /// And the default, which is the contract the rest of this suite depends on: off, silent, and
    /// no health-derived text in any line the client writes.
    /// </summary>
    [Fact]
    public async Task GenerateAsync_WritesNoPromptOrCompletion_WhenInspectionIsOff()
    {
        var handler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, GeneratePayload);
        var client = CreateClient(handler, out var logger);

        await client.GenerateAsync(Prompt);

        Assert.DoesNotContain(logger.Events, e => e.Event == MedGemmaClient.ClinicalInspectionEvent);
        Assert.All(logger.Entries, entry =>
        {
            Assert.DoesNotContain("chest pain", entry.Message);
            Assert.DoesNotContain(ResponseText, entry.Message);
        });
    }

    /// <summary>The DPIA regression pin, same as the MedGemma suite's.</summary>
    [Fact]
    public async Task GenerateAsync_NeverPutsPromptOrResponseTextOnTheSpan()
    {
        using var capture = new SpanCapture();
        var handler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, GeneratePayload);
        var client = CreateClient(handler, out _);

        await client.GenerateAsync(Prompt);

        var span = Assert.Single(capture.Stopped);
        foreach (var (_, value) in span.TagObjects)
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
        Assert.Equal("gcp.vertex_ai", duration.Tags["gen_ai.provider.name"]);
        Assert.False(duration.Tags.ContainsKey("error.type"));

        var tokens = metrics.Longs.Where(m => m.Instrument == "gen_ai.client.token.usage").ToList();
        Assert.Equal(2, tokens.Count);
        Assert.Equal(412, Assert.Single(tokens, t => Equals(t.Tags["gen_ai.token.type"], "input")).Value);
        Assert.Equal(128, Assert.Single(tokens, t => Equals(t.Tags["gen_ai.token.type"], "output")).Value);
    }

    /// <summary>
    /// The reply-schema dimension has to hold on both providers, not just the one where a
    /// truncation report first exposed the need for it: the rewrite slot's reads can be served by
    /// Vertex or by Ollama, and a dimension present on one of them cannot answer "what does this
    /// read normally produce" for either.
    /// </summary>
    [Fact]
    public async Task GenerateStructuredAsync_TagsTheSpanAndTokenMetrics_WithTheReplySchema()
    {
        using var capture = new SpanCapture();
        using var metrics = new MetricCapture();
        var payload =
            """
            {"candidates":[{"content":{"role":"model","parts":[{"text":"{\"sources\":[\"Digest\"],\"recentActivityDays\":3}"}]},
             "finishReason":"STOP"}],
             "usageMetadata":{"promptTokenCount":100,"candidatesTokenCount":20},"modelVersion":"gemini-2.5-flash-lite-001"}
            """;
        var handler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, payload);
        var client = CreateClient(handler, out _);

        await client.GenerateStructuredAsync<TestPlanShape>(Prompt);

        var span = Assert.Single(capture.Stopped);
        Assert.Equal(nameof(TestPlanShape), span.GetTagItem("carditrack.ai.reply_schema"));
        var duration = Assert.Single(metrics.Doubles, m => m.Instrument == "gen_ai.client.operation.duration");
        Assert.Equal(nameof(TestPlanShape), duration.Tags["carditrack.ai.reply_schema"]);
        var tokens = metrics.Longs.Where(m => m.Instrument == "gen_ai.client.token.usage").ToList();
        Assert.Equal(2, tokens.Count);
        Assert.All(tokens, t => Assert.Equal(nameof(TestPlanShape), t.Tags["carditrack.ai.reply_schema"]));
    }

    [Fact]
    public async Task GenerateStructuredAsync_SendsTheSchemaAsResponseJsonSchema_AndDeserializes()
    {
        var payload =
            """
            {"candidates":[{"content":{"role":"model","parts":[{"text":"{\"sources\":[\"Digest\"],\"recentActivityDays\":3}"}]},
             "finishReason":"STOP"}],
             "usageMetadata":{"promptTokenCount":100,"candidatesTokenCount":20},"modelVersion":"gemini-2.5-flash-lite-001"}
            """;
        var handler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, payload);
        var client = CreateClient(handler, out _);

        var result = await client.GenerateStructuredAsync<TestPlanShape>(Prompt);

        Assert.Equal(["Digest"], result.Sources);
        Assert.Equal(3, result.RecentActivityDays);

        var request = Assert.Single(handler.Requests);
        Assert.Contains("\"responseMimeType\":\"application/json\"", request.Body);
        // The schema is sent as responseJsonSchema (constrained decoding) AND appended to the
        // prompt — the exported nullable-int union must survive into the request verbatim.
        Assert.Contains("\"responseJsonSchema\":", request.Body);
        Assert.Contains("\"recentActivityDays\":{\"type\":[\"integer\",\"null\"]}", request.Body!.Replace(" ", ""));
        Assert.Contains("Respond with ONLY a single JSON object", request.Body);
    }

    [Fact]
    public async Task GenerateStructuredAsync_Throws_WithoutLeakingTheReply_WhenItDoesNotMatchTheSchema()
    {
        using var capture = new SpanCapture();
        var payload =
            """
            {"candidates":[{"content":{"role":"model","parts":[{"text":"not json at all, mentions chest pain"}]},
             "finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":100,"candidatesTokenCount":20}}
            """;
        var handler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, payload);
        var client = CreateClient(handler, out var logger);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GenerateStructuredAsync<TestPlanShape>(Prompt));

        Assert.DoesNotContain("chest pain", ex.Message);
        Assert.All(logger.Entries, e => Assert.DoesNotContain("chest pain", e.Message));
        var span = Assert.Single(capture.Stopped);
        foreach (var (_, value) in span.TagObjects)
            Assert.DoesNotContain("chest pain", value?.ToString() ?? string.Empty);
    }

    /// <summary>
    /// A reply that stopped at maxOutputTokens is a cut-off document, and handing it to the
    /// deserializer reported it as content that "could not be parsed" at whatever byte the cut
    /// landed on — the shape a live digest rewrite failed in (2026-09-16 and 2026-09-20: 8176
    /// output tokens against the 8192 ceiling, reported as a JSON error at '$.suggestion'). It is
    /// neither unparseable nor nonsense; it is unfinished, so the error says so and names the
    /// numbers, the same contract <see cref="MedGemmaClientTests"/> pins on the other slot.
    /// </summary>
    [Fact]
    public async Task GenerateStructuredAsync_ReportsTruncation_RatherThanBlamingTheJson()
    {
        var handler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, TruncatedStructuredPayload);
        var client = CreateClient(handler, out var logger);

        var ex = await Assert.ThrowsAsync<AiReplyTruncatedException>(
            () => client.GenerateStructuredAsync<TestPlanShape>(Prompt));

        Assert.Contains("token budget", ex.Message);
        Assert.DoesNotContain("could not be parsed", ex.Message);
        var error = Assert.Single(logger.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains("token budget", error.Message);
        Assert.Contains("8192", error.Message);
        Assert.Contains("did not stop", error.Message);
        // The trail that tells a loop from a reply needing more room is elapsed time, finish
        // reason and counts together, and this client writes them on one debug line — so the
        // truncation is reported after it, not instead of it.
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Debug && e.Message.Contains("MAX_TOKENS"));
    }

    /// <summary>
    /// The exception is typed and carries the counts because a caller has a decision the message
    /// alone cannot support: whether asking again is worth the inference. Nothing of the cut-off
    /// reply may travel with them — the DPIA invariant does not relax because the reply is broken.
    /// </summary>
    [Fact]
    public async Task GenerateStructuredAsync_TruncationCarriesTheCounts_WithoutTheReply()
    {
        using var capture = new SpanCapture();
        var handler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, TruncatedStructuredPayload);
        var client = CreateClient(handler, out var logger);

        var ex = await Assert.ThrowsAsync<AiReplyTruncatedException>(
            () => client.GenerateStructuredAsync<TestPlanShape>(Prompt));

        // Thoughts are generated output and count against the same ceiling: 8100 + 76.
        Assert.Equal(8176, ex.OutputTokens);
        Assert.Equal(8192, ex.MaxOutputTokens);
        Assert.Equal(1770, ex.InputTokens);
        Assert.Equal(nameof(TestPlanShape), ex.ReplySchema);
        // No client-set window on this provider — there is no ContextTokens here to raise.
        Assert.Null(ex.ContextTokens);
        // Every existing catch of a model-call failure keeps working: the digest pass logs the
        // rewrite failure and keeps the previous summary, as it did when this was a parse error.
        Assert.IsAssignableFrom<HttpRequestException>(ex);

        Assert.DoesNotContain("chest pain", ex.Message);
        Assert.All(logger.Entries, e => Assert.DoesNotContain("chest pain", e.Message));
        var span = Assert.Single(capture.Stopped);
        Assert.Equal("truncated", span.GetTagItem("error.type"));
        foreach (var (_, value) in span.TagObjects)
            Assert.DoesNotContain("chest pain", value?.ToString() ?? string.Empty);
    }

    /// <summary>
    /// MAX_TOKENS means the ceiling was reached, so a response that omits the token counts still
    /// reports the ceiling as the count — never zero, which would read as a reply that produced
    /// nothing, the opposite of what happened.
    /// </summary>
    [Fact]
    public async Task GenerateStructuredAsync_Truncation_ReportsTheCeiling_WhenTheResponseOmitsTheCounts()
    {
        var payload =
            """
            {"candidates":[{"content":{"role":"model","parts":[{"text":"{\"sources\":[\"Dig"}]},
             "finishReason":"MAX_TOKENS"}]}
            """;
        var handler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, payload);
        var client = CreateClient(handler, out var logger);

        var ex = await Assert.ThrowsAsync<AiReplyTruncatedException>(
            () => client.GenerateStructuredAsync<TestPlanShape>(Prompt));

        Assert.Equal(8192, ex.OutputTokens);
        Assert.Null(ex.InputTokens);
        Assert.Contains("8192 output token(s)", ex.Message);
        var error = Assert.Single(logger.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains("8192 output token(s)", error.Message);
    }

    /// <summary>
    /// Free text keeps what it got — a caregiver reading a reply that stops mid-sentence is worse
    /// than one cut short, but better than an error where an answer was nearly complete. The cut
    /// still goes on the record, or it is indistinguishable from a model that chose to stop.
    /// </summary>
    [Fact]
    public async Task GenerateAsync_ReturnsTheCutReply_AndLogsTheCut_WhenItStopsAtTheCeiling()
    {
        var payload =
            """
            {"candidates":[{"content":{"role":"model","parts":[{"text":"Trends look sta"}]},
             "finishReason":"MAX_TOKENS"}],
             "usageMetadata":{"promptTokenCount":412,"candidatesTokenCount":8192}}
            """;
        var handler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, payload);
        var client = CreateClient(handler, out var logger);

        var result = await client.GenerateAsync(Prompt);

        Assert.Equal("Trends look sta", result);
        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("cut short", warning.Message);
        Assert.Contains("8192", warning.Message);
        Assert.DoesNotContain("Trends look sta", warning.Message);
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task GenerateAsync_Throws_WithTheBlockReasonEnumOnly_WhenThePromptIsBlocked()
    {
        using var capture = new SpanCapture();
        var payload =
            """
            {"promptFeedback":{"blockReason":"PROHIBITED_CONTENT"},
             "usageMetadata":{"promptTokenCount":412}}
            """;
        var handler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, payload);
        var client = CreateClient(handler, out var logger);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GenerateAsync(Prompt));

        // The enum is Google's fixed vocabulary — safe; the prompt is not.
        Assert.Contains("PROHIBITED_CONTENT", ex.Message);
        Assert.DoesNotContain("chest pain", ex.Message);
        var span = Assert.Single(capture.Stopped);
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal("blocked_PROHIBITED_CONTENT", span.GetTagItem("error.type"));
        var error = Assert.Single(logger.Entries, e => e.Level == LogLevel.Error);
        Assert.DoesNotContain("chest pain", error.Message);
    }

    [Fact]
    public async Task GenerateAsync_Throws_WithTheFinishReasonEnumOnly_WhenTheGenerationIsStopped()
    {
        using var capture = new SpanCapture();
        var payload =
            """
            {"candidates":[{"content":{"role":"model","parts":[{"text":"partial sensitive output"}]},"finishReason":"SAFETY"}],
             "usageMetadata":{"promptTokenCount":412,"candidatesTokenCount":9}}
            """;
        var handler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, payload);
        var client = CreateClient(handler, out var logger);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GenerateAsync(Prompt));

        Assert.Contains("SAFETY", ex.Message);
        Assert.DoesNotContain("partial sensitive output", ex.Message);
        var span = Assert.Single(capture.Stopped);
        Assert.Equal("blocked_SAFETY", span.GetTagItem("error.type"));
        Assert.All(logger.Entries, e => Assert.DoesNotContain("partial sensitive output", e.Message));
    }

    [Fact]
    public async Task GenerateAsync_ExcludesThoughtParts_FromTheReturnedText()
    {
        var payload =
            """
            {"candidates":[{"content":{"role":"model","parts":[
              {"text":"internal reasoning","thought":true},
              {"text":"Trends look stable."}]},"finishReason":"STOP"}],
             "usageMetadata":{"promptTokenCount":412,"candidatesTokenCount":120}}
            """;
        var handler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, payload);
        var client = CreateClient(handler, out _);

        Assert.Equal(ResponseText, await client.GenerateAsync(Prompt));
    }

    [Fact]
    public async Task GenerateAsync_TagsSpanWithErrorType_AndRetriesToExhaustion_On500()
    {
        using var capture = new SpanCapture();
        var handler = new FakeHttpMessageHandler().Enqueue(
            HttpStatusCode.InternalServerError, "error body that echoes the prompt: chest pain");
        var client = CreateClient(handler, out var logger);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GenerateAsync(Prompt));

        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
        Assert.Equal(3, handler.Requests.Count);
        var span = Assert.Single(capture.Stopped);
        Assert.Equal("500", span.GetTagItem("error.type"));
        // The error body is never read: nothing anywhere may carry its content.
        Assert.All(logger.Entries, e => Assert.DoesNotContain("chest pain", e.Message));
    }

    [Fact]
    public async Task GenerateAsync_WaitsTheSaturationBackoff_For429()
    {
        var handler = new FakeHttpMessageHandler()
            .Enqueue(HttpStatusCode.TooManyRequests, "")
            .Enqueue(HttpStatusCode.OK, GeneratePayload);
        var client = CreateClient(handler, out _, out var time);

        await client.GenerateAsync(Prompt);

        // A quota window clears when it rolls over, not on an immediate re-ask.
        Assert.Equal(TimeSpan.FromSeconds(15), Assert.Single(time.Delays));
    }

    [Fact]
    public async Task GenerateAsync_HonoursRetryAfter_CappedAtTheMaxBackoff()
    {
        var handler = new FakeHttpMessageHandler()
            .Enqueue(_ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
                return response;
            })
            .Enqueue(HttpStatusCode.OK, GeneratePayload);
        var client = CreateClient(handler, out _, out var time);

        await client.GenerateAsync(Prompt);

        Assert.Equal(TimeSpan.FromSeconds(30), Assert.Single(time.Delays));
    }

    [Fact]
    public async Task GenerateAsync_RetriesATimeoutOnce_ThenGivesUp()
    {
        var handler = new FakeHttpMessageHandler().Throws(new TaskCanceledException());
        var client = CreateClient(handler, out _, out var time);

        await Assert.ThrowsAsync<TimeoutException>(() => client.GenerateAsync(Prompt));

        // One retry, not the three attempts an HTTP failure gets: a timeout is worth re-asking
        // once, and a second one says the far side is not going to answer inside the budget.
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(TimeSpan.FromSeconds(2), Assert.Single(time.Delays));
    }

    [Fact]
    public async Task GenerateAsync_RecoversFromASingleTimeout()
    {
        var handler = new FakeHttpMessageHandler()
            .Throws(new TaskCanceledException())
            .Enqueue(HttpStatusCode.OK, GeneratePayload);
        var client = CreateClient(handler, out _);

        Assert.Equal(ResponseText, await client.GenerateAsync(Prompt));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task GenerateAsync_LogsTheAttemptElapsed_NotTheWholeCallsElapsed_OnATimeout()
    {
        // The first attempt is made to burn real wall clock and the second none of it, which is
        // what makes the two figures separable: reporting the operation-wide stopwatch for both,
        // as the line used to, would close the gap asserted below. Nothing else in the call takes
        // measurable time — the retry backoff resolves instantly through the fake TimeProvider.
        var handler = new FakeHttpMessageHandler()
            .Enqueue(_ =>
            {
                Thread.Sleep(FirstAttemptDuration);
                throw new TaskCanceledException();
            })
            .Throws(new TaskCanceledException());
        var client = CreateClient(handler, out var logger);

        await Assert.ThrowsAsync<TimeoutException>(() => client.GenerateAsync(Prompt));

        // The terminal line names the attempt it belongs to and separates the two elapsed
        // figures, so "timed out after N ms (HttpClient.Timeout is 60 s)" can no longer read as
        // a contradiction when an earlier attempt and its backoff are inside N.
        var timedOut = Assert.Single(
            logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("timed out"));
        var figures = Regex.Match(
            timedOut.Message,
            @"timed out after (\d+) ms on attempt 2 of 3 "
            + @"\(HttpClient\.Timeout is 60 s; (\d+) ms since the call began\)");
        Assert.True(figures.Success, timedOut.Message);

        var attemptMs = int.Parse(figures.Groups[1].Value);
        var totalMs = int.Parse(figures.Groups[2].Value);

        // The call contains the first attempt's duration; the second attempt cannot. A slower
        // machine only widens this, so the margin is a floor rather than a window.
        Assert.True(
            totalMs - attemptMs >= FirstAttemptDuration.TotalMilliseconds / 2,
            $"attempt {attemptMs} ms, whole call {totalMs} ms — expected the call to exceed the "
            + $"attempt by most of the {FirstAttemptDuration.TotalMilliseconds} ms spent in the first one.");

        Assert.Contains(
            logger.Entries,
            e => e.Level == LogLevel.Warning && e.Message.Contains("retrying once"));
    }

    [Fact]
    public async Task GenerateAsync_PreservesCallerCancellation()
    {
        using var cts = new CancellationTokenSource();
        var handler = new FakeHttpMessageHandler().Enqueue(_ =>
        {
            cts.Cancel();
            throw new TaskCanceledException();
        });
        var client = CreateClient(handler, out _);

        await Assert.ThrowsAsync<TaskCanceledException>(() => client.GenerateAsync(Prompt, cts.Token));
    }

    [Fact]
    public async Task GenerateAsync_Throws_WithoutLeakingTheBody_WhenJsonIsMalformed()
    {
        var handler = new FakeHttpMessageHandler().Enqueue(
            HttpStatusCode.OK, "{malformed, mentions chest pain");
        var client = CreateClient(handler, out var logger);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GenerateAsync(Prompt));

        Assert.DoesNotContain("chest pain", ex.Message);
        Assert.All(logger.Entries, e => Assert.DoesNotContain("chest pain", e.Message));
    }

    [Fact]
    public async Task GenerateAsync_LogsTokenCounts_NeverPromptText()
    {
        var handler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, GeneratePayload);
        var client = CreateClient(handler, out var logger);

        await client.GenerateAsync(Prompt);

        // Debug, not Information — model/tokens/elapsed are already on the span tags and
        // AiTelemetry's metrics; this completion line is a local-troubleshooting aid, not
        // something worth shipping on every successful call.
        var debug = Assert.Single(logger.Entries, e => e.Level == LogLevel.Debug);
        Assert.Contains("412", debug.Message);
        Assert.DoesNotContain("chest pain", debug.Message);
        Assert.DoesNotContain(ResponseText, debug.Message);
    }

    /// <summary>Shape mirroring DataQueryPlanAiResponse's nullable-int properties — the case
    /// that broke llama.cpp's grammar compiler and pins the union handling here.</summary>
    private sealed record TestPlanShape
    {
        public required IReadOnlyList<string> Sources { get; init; }
        public int? RecentActivityDays { get; init; }
    }

    private static VertexAiClient CreateClient(FakeHttpMessageHandler handler, out ListLogger logger) =>
        CreateClient(handler, out logger, out _);

    private static VertexAiClient CreateClient(
        FakeHttpMessageHandler handler, out ListLogger logger, out InstantRetryTimeProvider time,
        bool logClinicalOutput = false)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("RewriteAiClient").Returns(
            new HttpClient(handler) { BaseAddress = new Uri("https://europe-west2-aiplatform.googleapis.com") });
        var options = new VertexAiClientOptions
        {
            Model = Model,
            ProjectId = "test-project",
            Location = "europe-west2",
            TimeoutSeconds = 60,
            MaxOutputTokens = 8192,
            LogClinicalOutput = logClinicalOutput,
        };
        logger = new ListLogger();
        time = new InstantRetryTimeProvider();
        return new VertexAiClient(factory, options, "RewriteAiClient", logger, time);
    }

    /// <summary>Same as the MedGemma suite's: backoffs are recorded, then resolve immediately.</summary>
    private sealed class InstantRetryTimeProvider : TimeProvider
    {
        public List<TimeSpan> Delays { get; } = new();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (Delays) Delays.Add(dueTime);
            return base.CreateTimer(callback, state, TimeSpan.Zero, period);
        }
    }

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
    private sealed class ListLogger : ILogger<VertexAiClient>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        /// <summary>
        /// Kept beside <see cref="Entries"/> rather than folded into it: the inspection outlet is
        /// identified by its event id, which is how a log pipeline selects or drops it, and a test
        /// that matched on the message text alone would pass a line written under any id at all.
        /// </summary>
        public List<(EventId Event, string Message)> Events { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Entries)
            {
                Entries.Add((logLevel, formatter(state, exception)));
                Events.Add((eventId, formatter(state, exception)));
            }
        }
    }
}
