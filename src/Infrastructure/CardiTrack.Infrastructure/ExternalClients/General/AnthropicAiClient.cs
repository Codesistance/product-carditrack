using Anthropic;
using Anthropic.Models.Messages;
using CardiTrack.Application.DTOs.Common;
using CardiTrack.Application.Interfaces.Clients;
using CardiTrack.Infrastructure.Settings;

namespace CardiTrack.Infrastructure.ExternalClients.General;

/// <summary>
/// Anthropic Messages API, via the official SDK.
/// </summary>
/// <remarks>
/// The sibling providers are hand-rolled against <see cref="IHttpClientFactory"/> because their
/// wire formats are a couple of fields wide. This one is not: max_tokens is mandatory on every
/// request, the system prompt is a top-level field rather than a message, and the response is a
/// block union rather than a string. The SDK owns those rules and its own transport, which is why
/// the shared named-HttpClient plumbing does not apply here.
/// Named with the Ai infix so it does not collide with the SDK's own AnthropicClient.
/// </remarks>
public class AnthropicAiClient : IExternalAiClient
{
    private readonly AnthropicClient _client;
    private readonly PublicAiSettings _settings;

    public AnthropicAiClient(AnthropicClient client, PublicAiSettings settings)
    {
        _client = client;
        _settings = settings;
    }

    public Task<string> GenerateAsync(string prompt, CancellationToken ct = default)
        => ChatAsync([], prompt, ct);

    public async Task<string> ChatAsync(IReadOnlyList<ChatMessage> history, string userMessage, CancellationToken ct = default)
    {
        var messages = history
            .Select(m => new MessageParam
            {
                Role = m.Role == ChatRole.User ? Role.User : Role.Assistant,
                Content = m.Content
            })
            .Append(new MessageParam { Role = Role.User, Content = userMessage })
            .ToList();

        var response = await _client.Messages.Create(new MessageCreateParams
        {
            Model = _settings.Model,
            MaxTokens = _settings.MaxOutputTokens,
            Messages = messages
        }, cancellationToken: ct);

        // A response is a list of blocks, not a string: thinking blocks precede text on models
        // that reason, and a long answer can arrive as several text blocks. Concatenating the text
        // ones is the whole answer; anything else in the list is not ours to render.
        var text = new System.Text.StringBuilder();
        foreach (var block in response.Content)
        {
            if (block.TryPickText(out TextBlock? textBlock))
                text.Append(textBlock.Text);
        }

        return text.ToString();
    }
}
