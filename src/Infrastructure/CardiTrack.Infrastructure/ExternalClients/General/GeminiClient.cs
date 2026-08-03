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
    private readonly AiProviderSettings _settings;

    public GeminiClient(IHttpClientFactory httpClientFactory, AiProviderSettings settings)
    {
        _httpClientFactory = httpClientFactory;
        _settings = settings;
    }

    public async Task<string> GenerateAsync(string prompt, CancellationToken ct = default)
    {
        return await ChatAsync([], prompt, ct);
    }

    public async Task<string> ChatAsync(IReadOnlyList<ChatMessage> history, string userMessage, CancellationToken ct = default)
    {
        var client = _httpClientFactory.CreateClient("GeminiClient");
        var endpoint = $"/v1beta/models/{_settings.Model}:generateContent?key={_settings.ApiKey}";

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

        var request = new GeminiRequest { Contents = contents };
        var response = await client.PostAsJsonAsync(endpoint, request, ct);
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
