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
        if (string.IsNullOrEmpty(jwt))
            return result;

        var parts = jwt.Split('.');
        if (parts.Length < 2)
            return result;

        string json;
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
        }
        catch (FormatException)
        {
            return result;
        }

        if (JsonUtility.TryParse(json, out var root, out _) && root is JObject claims)
        {
            foreach (var property in claims.Properties())
            {
                if (property.Value is JValue { Type: JTokenType.String } value)
                    result[property.Name] = (string?)value ?? string.Empty;
            }
        }

        return result;
    }
}
