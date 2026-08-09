using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using CardiTrack.Application.DTOs.Common;
using CardiTrack.Infrastructure.ExternalClients.Medical;
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
        var span = Assert.Single(capture.Stopped);
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal("500", span.GetTagItem("error.type"));

        var duration = Assert.Single(metrics.Doubles, m => m.Instrument == "gen_ai.client.operation.duration");
        Assert.Equal("500", duration.Tags["error.type"]);
        Assert.DoesNotContain(metrics.Longs, m => m.Instrument == "gen_ai.client.token.usage");

        var error = Assert.Single(logger.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains("500", error.Message);
        Assert.DoesNotContain("upstream error body", error.Message);
    }

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

    private static MedGemmaClient CreateClient(FakeHttpMessageHandler handler, out ListLogger logger)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("PrivateAiClient").Returns(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434") });
        var settings = new PrivateAiSettings { Model = Model, BaseUrl = "http://localhost:11434", TimeoutSeconds = 300 };
        logger = new ListLogger();
        return new MedGemmaClient(factory, settings, "PrivateAiClient", logger);
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
