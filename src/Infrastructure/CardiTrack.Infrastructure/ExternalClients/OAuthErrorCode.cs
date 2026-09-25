using CardiTrack.Shared.Json;
using Newtonsoft.Json.Linq;

namespace CardiTrack.Infrastructure.ExternalClients;

/// <summary>
/// The RFC 6749 §5.2 <c>error</c> code of a token-endpoint error body — the one part of that body
/// safe to put in an exception message.
/// </summary>
/// <remarks>
/// The refresh and exchange paths throw exceptions that every caller up the stack logs whole, so
/// their messages reach Datadog. The rest of an error body is the provider's free text
/// (<c>error_description</c>, <c>error_uri</c>, dialect extras) and is not ours to forward: a
/// token endpoint's body is the kind this codebase already treats as credential-bearing.
/// </remarks>
internal static class OAuthErrorCode
{
    /// <summary>
    /// The <c>error</c> code, or null for a body that is not JSON, carries no code, or carries
    /// one that is not code-shaped (lowercase letters, digits and underscores).
    /// </summary>
    public static string? Of(string body)
    {
        if (!JsonUtility.TryParse(body, out var root, out _) || root is not JObject envelope)
            return null;

        var code = envelope["error"] is JValue { Type: JTokenType.String } value ? (string?)value : null;
        return code is { Length: > 0 and <= 64 }
            && code.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_')
            ? code
            : null;
    }

    /// <summary>The code for a message, with a stand-in when the body had none.</summary>
    public static string Describe(string body) => Of(body) ?? "no error code";
}
