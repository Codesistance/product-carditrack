using System.Text;
using CardiTrack.Shared.Json;
using Newtonsoft.Json.Linq;

namespace CardiTrack.Mobile.Core.Auth;

/// <summary>
/// Decodes a JWT payload for display-only claims (name/email). No signature validation —
/// the token came straight from Auth0 over TLS and is never trusted for authorization here.
/// </summary>
public static class JwtPayloadReader
{
    public static IReadOnlyDictionary<string, string> ReadClaims(string? jwt)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (ReadPayload(jwt) is not { } claims)
            return result;

        foreach (var property in claims.Properties())
        {
            // Booleans surface as "true"/"false" — email_verified is a JSON bool.
            if (property.Value is JValue { Type: JTokenType.String } text)
                result[property.Name] = (string?)text ?? string.Empty;
            else if (property.Value is JValue { Type: JTokenType.Boolean } flag)
                result[property.Name] = (bool)flag ? "true" : "false";
        }

        return result;
    }

    /// <summary>
    /// The decoded payload object, or null when the token is absent, not a JWT, or
    /// carries a payload that isn't a JSON object. Shared with <see cref="AccessTokenAudience"/>,
    /// which needs claim shapes <see cref="ReadClaims"/> flattens away.
    /// </summary>
    internal static JObject? ReadPayload(string? jwt)
    {
        if (string.IsNullOrEmpty(jwt))
            return null;

        var parts = jwt.Split('.');
        if (parts.Length < 2)
            return null;

        string json;
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
        }
        catch (FormatException)
        {
            return null;
        }

        return JsonUtility.TryParse(json, out var root, out _) ? root as JObject : null;
    }
}
