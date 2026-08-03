using System.Net.Http.Json;
using System.Text.Json.Serialization;
using CardiTrack.Application.DTOs.Common;
using CardiTrack.Application.Interfaces.Clients;
using CardiTrack.Infrastructure.Settings;
using CardiTrack.Shared.Json;

namespace CardiTrack.Infrastructure.ExternalClients.Medical;

public class MedGemmaClient : IExternalAiClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AiProviderSettings _settings;

    public MedGemmaClient(IHttpClientFactory httpClientFactory, AiProviderSettings settings)
    {
        _httpClientFactory = httpClientFactory;
        _settings = settings;
    }

    public async Task<string> GenerateAsync(string prompt, CancellationToken ct = default)
    {
        var client = _httpClientFactory.CreateClient("MedGemmaClient");
        var request = new OllamaGenerateRequest { Model = _settings.Model, Prompt = prompt };
        var response = await client.PostAsJsonAsync("/api/generate", request, ct);
        response.EnsureSuccessStatusCode();
        var result = JsonUtility.Deserialize<OllamaGenerateResponse>(await response.Content.ReadAsStringAsync(ct));
        return result.Response ?? string.Empty;
    }

    public async Task<string> ChatAsync(IReadOnlyList<ChatMessage> history, string userMessage, CancellationToken ct = default)
    {
        var client = _httpClientFactory.CreateClient("MedGemmaClient");
        var messages = history
            .Select(m => new OllamaMessage { Role = m.Role == ChatRole.User ? "user" : "assistant", Content = m.Content })
            .Append(new OllamaMessage { Role = "user", Content = userMessage })
            .ToList();
        var request = new OllamaChatRequest { Model = _settings.Model, Messages = messages };
        var response = await client.PostAsJsonAsync("/api/chat", request, ct);
        response.EnsureSuccessStatusCode();
        var result = JsonUtility.Deserialize<OllamaChatResponse>(await response.Content.ReadAsStringAsync(ct));
        return result.Message?.Content ?? string.Empty;
    }

    private record OllamaGenerateRequest
    {
        [JsonPropertyName("model")] public required string Model { get; init; }
        [JsonPropertyName("prompt")] public required string Prompt { get; init; }
        [JsonPropertyName("stream")] public bool Stream { get; init; } = false;
    }

    private record OllamaGenerateResponse
    {
        [JsonPropertyName("response")] public string? Response { get; init; }
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

    private record OllamaChatResponse
    {
        [JsonPropertyName("message")] public OllamaMessage? Message { get; init; }
    }
}
