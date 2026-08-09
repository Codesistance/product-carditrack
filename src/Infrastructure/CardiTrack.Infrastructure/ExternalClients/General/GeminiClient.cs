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
