using System.Net.Http.Json;
using System.Text.Json.Serialization;
using CardiTrack.Application.DTOs.Common;
using CardiTrack.Application.Interfaces.Clients;
using CardiTrack.Infrastructure.Settings;
using CardiTrack.Shared.Json;

namespace CardiTrack.Infrastructure.ExternalClients.General;

public class GeminiClient : IExternalAiClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly PublicAiSettings _settings;
    private readonly string _httpClientName;

    public GeminiClient(IHttpClientFactory httpClientFactory, PublicAiSettings settings, string httpClientName)
    {
        _httpClientFactory = httpClientFactory;
        _settings = settings;
        _httpClientName = httpClientName;
    }

    public async Task<string> GenerateAsync(string prompt, CancellationToken ct = default)
    {
        return await ChatAsync([], prompt, ct);
    }

    // Gemini's responseSchema (generationConfig.responseSchema) could support this, but nothing on
    // the public/general provider needs structured output today — only the medical prompts
    // (always MedGemmaClient) do. Implement for real when a public-provider caller needs it, rather
    // than leaving a schema translation untested against no actual use.
    public Task<T> GenerateStructuredAsync<T>(string prompt, CancellationToken ct = default) where T : class =>
        throw new NotSupportedException(
            $"{nameof(GeminiClient)} does not support structured output yet — no caller needs it.");

    // Only member chat reads usage today, and member chat never reaches the public provider (see
    // MemberChatService) — so this returns the model name with null token counts rather than
    // parsing Gemini's usageMetadata, the same "no caller needs it yet" call as
    // GenerateStructuredAsync above. Parse it for real the day a public-provider caller needs it.
    public async Task<AiGenerationResult<string>> GenerateWithUsageAsync(string prompt, CancellationToken ct = default)
        => new(await GenerateAsync(prompt, ct), new AiUsage { ModelName = _settings.Model });

    public Task<AiGenerationResult<T>> GenerateStructuredWithUsageAsync<T>(
        string prompt, CancellationToken ct = default) where T : class =>
        throw new NotSupportedException(
            $"{nameof(GeminiClient)} does not support structured output yet — no caller needs it.");

    public async Task<string> ChatAsync(IReadOnlyList<ChatMessage> history, string userMessage, CancellationToken ct = default)
    {
        var client = _httpClientFactory.CreateClient(_httpClientName);

        var contents = history
            .Select(m => new GeminiContent
            {
                Role = m.Role == ChatRole.User ? "user" : "model",
                Parts = [new GeminiPart { Text = m.Content }]
            })
            .Append(new GeminiContent
            {
                Role = "user",
                Parts = [new GeminiPart { Text = userMessage }]
            })
            .ToList();

        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/v1beta/models/{_settings.Model}:generateContent")
        {
            Content = JsonContent.Create(new GeminiRequest { Contents = contents })
        };

        // Header rather than a query-string key: query strings are the one part of a URL that
        // routinely lands in proxy and access logs we do not control, and this key is the whole
        // credential. Google accepts either form.
        request.Headers.Add("x-goog-api-key", _settings.ApiKey);

        var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var result = JsonUtility.Deserialize<GeminiResponse>(await response.Content.ReadAsStringAsync(ct));
        return result.Candidates?.FirstOrDefault()?.Content?.Parts?.FirstOrDefault()?.Text ?? string.Empty;
    }

    private record GeminiRequest
    {
        [JsonPropertyName("contents")] public required List<GeminiContent> Contents { get; init; }
    }

    private record GeminiContent
    {
        [JsonPropertyName("role")] public required string Role { get; init; }
        [JsonPropertyName("parts")] public required List<GeminiPart> Parts { get; init; }
    }

    private record GeminiPart
    {
        [JsonPropertyName("text")] public required string Text { get; init; }
    }

    private record GeminiResponse
    {
        [JsonPropertyName("candidates")] public List<GeminiCandidate>? Candidates { get; init; }
    }

    private record GeminiCandidate
    {
        [JsonPropertyName("content")] public GeminiContent? Content { get; init; }
    }
}
