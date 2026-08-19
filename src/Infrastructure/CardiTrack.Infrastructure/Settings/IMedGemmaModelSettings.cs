namespace CardiTrack.Infrastructure.Settings;

/// <summary>
/// The part of a settings class <see cref="ExternalClients.Medical.MedGemmaClient"/> actually
/// reads — <c>Model</c> per call and <c>TimeoutSeconds</c> for its error messages. Everything
/// else (<c>BaseUrl</c>, <c>UseIdentityToken</c>) is consumed outside the client, when wiring the
/// named <see cref="HttpClient"/> it is handed.
/// </summary>
/// <remarks>
/// Exists so <see cref="PrivateAiSettings"/> and <see cref="RewriteAiSettings"/> can share one
/// <see cref="ExternalClients.Medical.MedGemmaClient"/> implementation — two model tags on the
/// same in-project host — without the client depending on either settings type by name.
/// </remarks>
public interface IMedGemmaModelSettings
{
    string Model { get; }
    int TimeoutSeconds { get; }
}
