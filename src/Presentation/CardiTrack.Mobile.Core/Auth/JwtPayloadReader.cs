using System.Text.Json;

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

        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                    result[property.Name] = property.Value.GetString() ?? string.Empty;
            }
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
        }

        return result;
    }
}
