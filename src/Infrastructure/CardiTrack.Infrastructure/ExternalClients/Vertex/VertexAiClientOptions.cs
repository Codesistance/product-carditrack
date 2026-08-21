namespace CardiTrack.Infrastructure.ExternalClients.Vertex;

/// <summary>
/// Everything <see cref="VertexAiClient"/> needs to address one model, independent of which
/// settings section it came from. Both the Rewrite slot (<see cref="Settings.RewriteAiSettings"/>)
/// and the Public slot (<see cref="Settings.PublicAiSettings"/>) can select Vertex, and building
/// this record in the registration keeps the client itself coupled to neither settings type —
/// the same seam <see cref="Settings.IMedGemmaModelSettings"/> provides for the Ollama client.
/// </summary>
public sealed record VertexAiClientOptions
{
    /// <summary>Publisher model id, e.g. <c>gemini-2.5-flash-lite</c>.</summary>
    public required string Model { get; init; }

    /// <summary>GCP project the call is billed to and authorised against.</summary>
    public required string ProjectId { get; init; }

    /// <summary>
    /// Regional location, e.g. <c>europe-west2</c>. Also selects the regional endpoint host when
    /// <see cref="BaseUrl"/> is not overridden. Never <c>global</c>: the DPIA pins AI processing
    /// to EU regions, and the global endpoint may route anywhere.
    /// </summary>
    public required string Location { get; init; }

    public required int TimeoutSeconds { get; init; }

    public required int MaxOutputTokens { get; init; }

    /// <summary>Endpoint override for test doubles; derived from <see cref="Location"/> when null.</summary>
    public string? BaseUrl { get; init; }

    /// <summary>The regional endpoint this configuration reaches.</summary>
    public string ResolveBaseUrl() => BaseUrl ?? $"https://{Location}-aiplatform.googleapis.com";
}
