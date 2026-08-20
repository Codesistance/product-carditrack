namespace CardiTrack.Application.DTOs.Common;

/// <summary>
/// A generated result paired with the usage of the call that produced it. Exists alongside the
/// plain <c>GenerateAsync</c>/<c>GenerateStructuredAsync</c> methods on
/// <see cref="CardiTrack.Application.Interfaces.Clients.IExternalAiClient"/> rather than replacing
/// them, so no existing caller has to change to accommodate a need only member chat has — see that
/// interface's remarks.
/// </summary>
public sealed record AiGenerationResult<T>(T Result, AiUsage Usage);
