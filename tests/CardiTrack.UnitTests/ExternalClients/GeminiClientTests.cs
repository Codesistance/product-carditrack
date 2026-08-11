using System.Net;
using System.Text;
using CardiTrack.Application.DTOs.Common;
using CardiTrack.Infrastructure.ExternalClients.General;
using CardiTrack.Infrastructure.Settings;
using NSubstitute;

namespace CardiTrack.UnitTests.ExternalClients;

public class GeminiClientTests
{
    /// <summary>Captures the outgoing request so the test can assert on it after the client disposes it.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly string _body;

        public CapturingHandler(string body) => _body = body;

        public Uri? RequestUri { get; private set; }
        public string? ApiKeyHeader { get; private set; }
        public string? SentBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            ApiKeyHeader = request.Headers.TryGetValues("x-goog-api-key", out var values)
                ? string.Join(",", values)
                : null;
            SentBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            };
        }
    }

    private const string SuccessBody =
        """{"candidates":[{"content":{"role":"model","parts":[{"text":"Steps are trending up."}]}}]}""";

    /// <summary>
    /// The key is the entire credential for this endpoint, and query strings are the one part of a
    /// URL that routinely lands in proxy and access logs we do not control. Header only.
    /// </summary>
    [Fact]
    public async Task ChatAsync_SendsTheApiKeyAsAHeader_AndNeverInTheUrl()
    {
        var handler = new CapturingHandler(SuccessBody);

        await Client(handler).ChatAsync([], "How are the steps?");

        Assert.Equal("secret-key", handler.ApiKeyHeader);
        Assert.DoesNotContain("secret-key", handler.RequestUri!.ToString());
        Assert.DoesNotContain("key=", handler.RequestUri.Query);
    }

    [Fact]
    public async Task ChatAsync_TargetsTheConfiguredModel()
    {
        var handler = new CapturingHandler(SuccessBody);

        await Client(handler).ChatAsync([], "How are the steps?");

        Assert.Equal("/v1beta/models/gemini-2.0-flash:generateContent", handler.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task ChatAsync_AppendsTheUserMessageAfterTheHistory()
    {
        var handler = new CapturingHandler(SuccessBody);
        var history = new List<ChatMessage>
        {
            new() { Role = ChatRole.User, Content = "first" },
            new() { Role = ChatRole.Model, Content = "second" }
        };

        await Client(handler).ChatAsync(history, "third");

        var body = handler.SentBody!;
        Assert.True(body.IndexOf("first", StringComparison.Ordinal) < body.IndexOf("second", StringComparison.Ordinal));
        Assert.True(body.IndexOf("second", StringComparison.Ordinal) < body.IndexOf("third", StringComparison.Ordinal));
        // Gemini names the assistant role "model", not "assistant".
        Assert.Contains("\"role\":\"model\"", body);
    }

    [Fact]
    public async Task ChatAsync_ReturnsTheFirstCandidatesText()
    {
        var reply = await Client(new CapturingHandler(SuccessBody)).ChatAsync([], "How are the steps?");

        Assert.Equal("Steps are trending up.", reply);
    }

    /// <summary>A response with no candidates is empty, not an exception — the caller renders nothing.</summary>
    [Fact]
    public async Task ChatAsync_ReturnsEmpty_WhenThereAreNoCandidates()
    {
        var reply = await Client(new CapturingHandler("""{"candidates":[]}""")).ChatAsync([], "anything");

        Assert.Equal(string.Empty, reply);
    }

    [Fact]
    public async Task ChatAsync_Throws_OnAnErrorStatus()
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(new ErrorHandler())
        {
            BaseAddress = new Uri("https://generativelanguage.googleapis.com")
        });

        var client = new GeminiClient(factory, Settings(), "PublicAiClient");

        await Assert.ThrowsAsync<HttpRequestException>(() => client.ChatAsync([], "anything"));
    }

    private sealed class ErrorHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
    }

    private sealed record TestStructuredResponse
    {
        public required string Summary { get; init; }
    }

    // Structured output isn't wired up on the public provider yet — nothing calls it. The
    // NotSupportedException is a placeholder that must fail loudly, not silently return free text
    // mis-shaped as T.
    [Fact]
    public async Task GenerateStructuredAsync_ThrowsNotSupported()
    {
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            Client(new CapturingHandler(SuccessBody)).GenerateStructuredAsync<TestStructuredResponse>("anything"));
    }

    private static GeminiClient Client(HttpMessageHandler handler)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler)
        {
            BaseAddress = new Uri("https://generativelanguage.googleapis.com")
        });

        return new GeminiClient(factory, Settings(), "PublicAiClient");
    }

    private static PublicAiSettings Settings() => new()
    {
        Kind = PublicAiProviderKind.Gemini,
        Model = "gemini-2.0-flash",
        ApiKey = "secret-key"
    };
}
