namespace CardiTrack.Infrastructure.Settings;

/// <summary>
/// Wire protocols the rewrite provider (AI:Rewrite) can speak. Like
/// <see cref="PublicAiProviderKind"/>, a closed enum: an unrecognised value is a deployment error
/// to fail on at startup, not a provider we could plausibly reach.
/// </summary>
/// <remarks>
/// The rewrite slot was in-estate-only (Ollama on Cloud Run) until 2026-08; it may now also run on
/// Google Vertex AI under the updated DPIA (row A20): the rewrite-slot prompts carry the
/// caregiver's question and MedGemma's de-identified clinical read — never the member's name, id,
/// notes or questionnaire answers — and Vertex processes them on an EU regional endpoint under the
/// Google Cloud DPA with the project's zero-data-retention configuration
/// (docs/technical/vertex_ai_setup.md). The clinical slot (<see cref="PrivateAiSettings"/>)
/// remains pinned in-estate with no kind switch.
/// </remarks>
public enum RewriteAiProviderKind
{
    /// <summary>Self-hosted Ollama — the local-dev default; no GCP credentials involved.</summary>
    Ollama,

    /// <summary>Gemini on Vertex AI, addressed by project/location, authorised by IAM.</summary>
    VertexGemini,
}
