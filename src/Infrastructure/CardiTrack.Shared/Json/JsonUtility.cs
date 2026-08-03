using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CardiTrack.Shared.Json;

/// <summary>One tracked error from a JSON operation: where it happened and why.</summary>
public sealed record JsonError(string Path, int LineNumber, int LinePosition, string Message)
{
    public override string ToString() =>
        $"{(Path.Length == 0 ? "$" : Path)} (line {LineNumber}, pos {LinePosition}): {Message}";
}

/// <summary>
/// Thrown by the strict <see cref="JsonUtility"/> methods, carrying every recorded error
/// plus a truncated preview of the payload that failed — the message is meant to be enough
/// to reproduce and fix the problem without re-running the request.
/// </summary>
public sealed class JsonDeserializationException : Exception
{
    public IReadOnlyList<JsonError> Errors { get; }

    /// <summary>The failing input, truncated per <see cref="JsonUtility.PreviewOf"/>.</summary>
    public string PayloadPreview { get; }

    public JsonDeserializationException(string target, IReadOnlyList<JsonError> errors, string payloadPreview)
        : base($"Failed to deserialize JSON to {target}: " +
               $"{(errors.Count == 0 ? "the payload produced no value" : string.Join("; ", errors))}. " +
               $"Payload: {payloadPreview}")
    {
        Errors = errors;
        PayloadPreview = payloadPreview;
    }
}

/// <summary>
/// The solution's single JSON deserialization entry point, built on Newtonsoft.Json only.
/// Every failure is tracked as a <see cref="JsonError"/> (path + line + position): member-level
/// errors are collected and deserialization continues, so one call reports everything wrong
/// with a payload instead of only the first problem. Syntax errors terminate the read but are
/// captured with their location. Property matching is case-insensitive (Newtonsoft default);
/// name mappings beyond that use [JsonProperty].
/// Serialization is intentionally out of scope — this class owns reading JSON, not writing it.
/// </summary>
public static class JsonUtility
{
    private const int MaxErrorEvents = 64;
    private const int MaxPreviewLength = 1000;

    /// <summary>
    /// The failing string as it should appear in errors and logs: verbatim up to
    /// 1000 chars, then truncated with the original length noted. Do NOT feed
    /// credential-bearing payloads (token responses, secrets) through this into
    /// logs — report their length and error locations instead.
    /// </summary>
    public static string PreviewOf(string? json) =>
        string.IsNullOrEmpty(json)
            ? "<empty>"
            : json.Length <= MaxPreviewLength
                ? json
                : $"{json[..MaxPreviewLength]}… (truncated, {json.Length} chars total)";

    /// <summary>Strict: returns a non-null value or throws <see cref="JsonDeserializationException"/> with all errors and the payload.</summary>
    public static T Deserialize<T>(string? json)
    {
        if (!TryDeserialize<T>(json, out var value, out var errors))
            throw new JsonDeserializationException(typeof(T).Name, errors, PreviewOf(json));
        return value!;
    }

    /// <summary>
    /// Lenient: false (with the full error list) instead of throwing. On member-level errors
    /// <paramref name="value"/> may still hold the partially populated object.
    /// </summary>
    public static bool TryDeserialize<T>(string? json, out T? value, out IReadOnlyList<JsonError> errors)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(json))
        {
            errors = [EmptyInput()];
            return false;
        }

        var tracked = new List<JsonError>();
        var seen = new HashSet<Exception>();
        var errorEvents = 0;
        var settings = new JsonSerializerSettings
        {
            Error = (_, args) =>
            {
                errorEvents++;
                if (seen.Add(args.ErrorContext.Error))
                {
                    var error = ToError(args.ErrorContext.Path, args.ErrorContext.Error);
                    // A stuck reader re-raises the same failure from the same spot — fold
                    // consecutive duplicates so the list stays meaningful.
                    if (tracked.Count == 0 || tracked[^1] != error)
                        tracked.Add(error);
                }
                // Collect-and-continue so one pass reports every problem in the payload.
                // The event cap is the termination guarantee: a reader that cannot make
                // progress stops being handled and the outer catch ends the read.
                args.ErrorContext.Handled = errorEvents < MaxErrorEvents;
            },
        };

        try
        {
            value = JsonConvert.DeserializeObject<T>(json, settings);
        }
        catch (Exception ex) when (ex is JsonException)
        {
            if (seen.Add(ex))
            {
                var error = ToError(path: string.Empty, ex);
                if (tracked.Count == 0 || tracked[^1] != error)
                    tracked.Add(error);
            }
        }

        errors = tracked;
        return tracked.Count == 0 && value is not null;
    }

    /// <summary>Strict parse for probe-style access (token["a"]?["b"]). Throws with tracked errors and the payload.</summary>
    public static JToken Parse(string? json)
    {
        if (!TryParse(json, out var token, out var errors))
            throw new JsonDeserializationException(nameof(JToken), errors, PreviewOf(json));
        return token!;
    }

    /// <summary>Lenient parse for probe-style access. Dates stay raw strings — probing code decides how to interpret them.</summary>
    public static bool TryParse(string? json, out JToken? token, out IReadOnlyList<JsonError> errors)
    {
        token = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            errors = [EmptyInput()];
            return false;
        }

        try
        {
            using var reader = new JsonTextReader(new StringReader(json))
            {
                DateParseHandling = DateParseHandling.None,
            };
            token = JToken.ReadFrom(reader, new JsonLoadSettings { LineInfoHandling = LineInfoHandling.Load });
            errors = [];
            return true;
        }
        catch (JsonException ex)
        {
            errors = [ToError(path: string.Empty, ex)];
            return false;
        }
    }

    private static JsonError EmptyInput() => new(string.Empty, 0, 0, "JSON input was null or empty.");

    private static JsonError ToError(string path, Exception error) => error switch
    {
        JsonReaderException reader => new JsonError(
            string.IsNullOrEmpty(path) ? reader.Path ?? string.Empty : path,
            reader.LineNumber, reader.LinePosition, reader.Message),
        JsonSerializationException serialization => new JsonError(
            string.IsNullOrEmpty(path) ? serialization.Path ?? string.Empty : path,
            serialization.LineNumber, serialization.LinePosition, serialization.Message),
        _ => new JsonError(path, 0, 0, error.Message),
    };
}
