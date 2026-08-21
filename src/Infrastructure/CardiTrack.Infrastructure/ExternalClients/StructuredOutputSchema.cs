using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace CardiTrack.Infrastructure.ExternalClients;

/// <summary>
/// Generates the JSON Schema text every structured-output call sends, and holds the serializer
/// options that both produce that schema and parse the model's reply. Shared between
/// <see cref="Medical.MedGemmaClient"/> (Ollama grammar-constrained decoding) and
/// <see cref="Vertex.VertexAiClient"/> (Vertex <c>responseJsonSchema</c>) so the two providers
/// cannot drift apart on what a response type's wire shape is.
/// </summary>
internal static class StructuredOutputSchema
{
    /// <summary>
    /// One JSON Schema document per response type, generated once via reflection and reused for
    /// the lifetime of the process — every call site for a given type asks for the same shape, so
    /// there is nothing to invalidate. Cached as text, not as a <see cref="System.Text.Json.Nodes.JsonNode"/>:
    /// a <see cref="System.Text.Json.Nodes.JsonNode"/> tree lazily materialises internal state on
    /// first access, which is not safe to share across concurrent requests, whereas re-parsing an
    /// immutable string per call is cheap and race-free.
    /// </summary>
    private static readonly ConcurrentDictionary<Type, string> Cache = new();

    /// <summary>
    /// camelCase, case-insensitive: matches typical JSON API convention, and keeps the schema's
    /// property names, the prompt's description of them, and each client's deserialization of
    /// the model's reply reading from one naming policy throughout. The explicit
    /// <see cref="DefaultJsonTypeInfoResolver"/> is required by <see cref="JsonSchemaExporter"/> —
    /// without it, options built from <see cref="JsonSerializerDefaults"/> alone throw at export time.
    /// </summary>
    /// <remarks>
    /// <see cref="JsonNumberHandling.Strict"/> overrides the Web default (numbers-from-strings)
    /// because these options are also the schema's source, and the two requirements collide:
    /// under the Web default the exporter renders every numeric property as
    /// <c>"type":["string","integer"]</c> plus a <c>pattern</c> regex containing <c>\d</c>, and
    /// llama.cpp's grammar compiler rejects that escape — Ollama answers 400
    /// "failed to parse grammar" for any type with a numeric field (member chat's query-plan
    /// step, found 2026-08-20). Strict exports plain <c>"type":"integer"</c>, and the constraint
    /// then guarantees the reply's numbers are real JSON numbers, so strict parsing of the reply
    /// is consistent by construction. Vertex's <c>responseJsonSchema</c> likewise rejects
    /// <c>pattern</c>-bearing string-or-number unions, so the same export shape serves both.
    /// </remarks>
    internal static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        NumberHandling = JsonNumberHandling.Strict,
    };

    /// <summary>
    /// Copies each property's <see cref="DescriptionAttribute"/> into its schema node.
    /// <see cref="JsonSchemaExporter"/> emits names and types only, so without this the schema
    /// appended to the prompt states the <em>shape</em> of the reply and nothing about what belongs
    /// in each field — leaving a bare field name as the sole description of its own contents, which
    /// a small model will happily satisfy by restating the brief it was just given. Callers that
    /// decorate nothing export exactly what they exported before.
    /// </summary>
    private static readonly JsonSchemaExporterOptions DescribedSchemaOptions = new()
    {
        TransformSchemaNode = (context, schema) =>
        {
            var description = context.PropertyInfo?.AttributeProvider
                ?.GetCustomAttributes(typeof(DescriptionAttribute), inherit: true)
                .OfType<DescriptionAttribute>()
                .FirstOrDefault()?.Description;

            if (string.IsNullOrWhiteSpace(description))
                return schema;

            // An unconstrained node is exported as the boolean `true`, not an object; assigning a
            // property to that would throw, and `{"description": ...}` says the same thing.
            var node = schema as System.Text.Json.Nodes.JsonObject ?? new System.Text.Json.Nodes.JsonObject();
            node["description"] = description;
            return node;
        },
    };

    // Internal (not private) so StructuredSchemaGrammarTests asserts against the real
    // generator rather than a re-implementation that could drift.
    internal static string TextFor<T>() => Cache.GetOrAdd(typeof(T), type =>
        JsonSchemaExporter.GetJsonSchemaAsNode(SerializerOptions, type, DescribedSchemaOptions)
            .ToJsonString(SerializerOptions));
}
