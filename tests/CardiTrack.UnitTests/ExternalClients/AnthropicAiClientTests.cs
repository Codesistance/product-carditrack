using System.Net;
using System.Text;
using System.Text.Json;
using CardiTrack.Application.DTOs.Common;
using CardiTrack.Infrastructure.ExternalClients.General;
using CardiTrack.Infrastructure.Settings;

namespace CardiTrack.UnitTests.ExternalClients;

/// <summary>
/// Wire-level coverage for the Anthropic provider, driven through the SDK's own HttpClient hook so
/// the request the SDK actually builds is what gets asserted.
/// </summary>
public class AnthropicAiClientTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly string _body;

        public CapturingHandler(string body) => _body = body;

        public string? SentBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SentBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            };
        }
    }

    private static string Reply(params string[] paragraphs)
    {
        var blocks = string.Join(",", paragraphs.Select(p =>
            "{\"type\":\"text\",\"text\":" + JsonSerializer.Serialize(p) + "}"));

        return "{\"id\":\"msg_01\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-opus-5\","
             + "\"content\":[" + blocks + "],\"stop_reason\":\"end_turn\","
             + "\"usage\":{\"input_tokens\":10,\"output_tokens\":20}}";
    }

    /// <summary>
    /// max_tokens is mandatory on this API — a request without it is rejected outright, so the
    /// configured ceiling has to reach the wire.
    /// </summary>
    [Fact]
    public async Task ChatAsync_SendsTheConfiguredModelAndTokenCeiling()
    {
        var handler = new CapturingHandler(Reply("ok"));

        await Client(handler).ChatAsync([], "How are the steps?");

        using var sent = JsonDocument.Parse(handler.SentBody!);
        Assert.Equal("claude-opus-5", sent.RootElement.GetProperty("model").GetString());
        Assert.Equal(16000, sent.RootElement.GetProperty("max_tokens").GetInt32());
    }

    /// <summary>
    /// Our transcript calls the assistant role "model" (Gemini's vocabulary); Anthropic will reject
    /// anything but "user" and "assistant", so the mapping has to happen here.
    /// </summary>
    [Fact]
    public async Task ChatAsync_MapsTheModelRoleToAssistant()
    {
        var handler = new CapturingHandler(Reply("ok"));
        var history = new List<ChatMessage>
        {
            new() { Role = ChatRole.User, Content = "first" },
            new() { Role = ChatRole.Model, Content = "second" }
        };

        await Client(handler).ChatAsync(history, "third");

        using var sent = JsonDocument.Parse(handler.SentBody!);
        var messages = sent.RootElement.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(3, messages.Count);
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
        Assert.Equal("assistant", messages[1].GetProperty("role").GetString());
        Assert.Equal("user", messages[2].GetProperty("role").GetString());
        Assert.DoesNotContain("\"role\":\"model\"", handler.SentBody!);
    }

    [Fact]
    public async Task ChatAsync_ReturnsTheTextBlock()
    {
        var reply = await Client(new CapturingHandler(Reply("Steps are trending up."))).ChatAsync([], "How are the steps?");

        Assert.Equal("Steps are trending up.", reply);
    }

    /// <summary>A long answer arrives as several text blocks; the reply is all of them, in order.</summary>
    [Fact]
    public async Task ChatAsync_ConcatenatesEveryTextBlock()
    {
        var reply = await Client(new CapturingHandler(Reply("Steps are up. ", "Sleep is flat."))).ChatAsync([], "anything");

        Assert.Equal("Steps are up. Sleep is flat.", reply);
    }

    [Fact]
    public async Task GenerateAsync_SendsASingleUserMessage()
    {
        var handler = new CapturingHandler(Reply("ok"));

        await Client(handler).GenerateAsync("Write a report.");

        using var sent = JsonDocument.Parse(handler.SentBody!);
        var messages = sent.RootElement.GetProperty("messages").EnumerateArray().ToList();
        Assert.Single(messages);
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
    }

    private static AnthropicAiClient Client(HttpMessageHandler handler)
    {
        var sdk = new Anthropic.AnthropicClient
        {
            ApiKey = "secret-key",
            BaseUrl = "https://api.anthropic.com",
            HttpClient = new HttpClient(handler)
        };

        return new AnthropicAiClient(sdk, new PublicAiSettings
        {
            Kind = PublicAiProviderKind.Anthropic,
            Model = "claude-opus-5",
            ApiKey = "secret-key",
            MaxOutputTokens = 16000
        });
    }
}
