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
    /// <para>
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
    /// </para>
    /// <para>
    /// The relaxed encoder is here because this schema is not only a wire contract: its text is
    /// appended to the prompt, so a model reads the field descriptions as English. The default
    /// encoder escapes every non-ASCII character and the apostrophe to a <c>\uXXXX</c> sequence,
    /// which stays escaped all the way to the model — it was being handed a description reading
    /// "CardiTrackCardiMember's whole day" in the middle of the sentence telling it what to
    /// write. Escaping still happens where it matters: the schema travels to the provider inside
    /// a request body serialised by that client's own options.
    /// </para>
    /// </remarks>
    internal static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        NumberHandling = JsonNumberHandling.Strict,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
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
    internal static string TextFor<T>() => TextFor(typeof(T));

    internal static string TextFor(Type type) => Cache.GetOrAdd(type, t =>
    {
        var node = JsonSchemaExporter.GetJsonSchemaAsNode(SerializerOptions, t, DescribedSchemaOptions);
        RequireAnObjectAtTheRoot(node);
        return node.ToJsonString(SerializerOptions);
    });

    /// <summary>
    /// Drops <c>null</c> from the root type union, so the schema says the reply is an object.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every response type is a reference type, and the exporter has no nullability annotation to
    /// read at the root, so it exports <c>{"type":["object","null"], …}</c> for all of them. Both
    /// providers compile that into the decoding grammar, which makes a bare <c>null</c> a legal
    /// reply — and a legal <em>first token</em>, on a prompt the model may find hard.
    /// </para>
    /// <para>
    /// It is also the one reply neither client can do anything with: deserializing <c>null</c>
    /// into the response type yields null, which both turn into a failed generation. So the
    /// constraint whose entire purpose is to stop the model producing something unusable was
    /// advertising the unusable answer as an option. Removing it costs nothing — nothing wanted a
    /// null reply — and closes the branch in the grammar rather than catching it afterwards.
    /// </para>
    /// <para>
    /// Only the root. A nullable <em>property</em> is a real part of the contract: a daybook with
    /// no suggestion worth making says so with a null, and the schema must keep letting it.
    /// </para>
    /// </remarks>
    private static void RequireAnObjectAtTheRoot(System.Text.Json.Nodes.JsonNode node)
    {
        if (node is not System.Text.Json.Nodes.JsonObject root
            || root["type"] is not System.Text.Json.Nodes.JsonArray union)
        {
            return;
        }

        var named = union.OfType<System.Text.Json.Nodes.JsonValue>()
            .Select(value => value.GetValue<string>())
            .Where(name => name != "null")
            .ToList();

        // Anything other than exactly "object" left over is a shape this was not written for;
        // leave it alone rather than guess.
        if (named is ["object"])
            root["type"] = "object";
    }

    /// <summary>
    /// The sentence that introduces the schema in the prompt, and the schema after it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Here rather than in each client. Both wrote this sentence out in full, directly beneath a
    /// comment explaining that the schema and the prompt's description of it cannot drift apart
    /// because both come from the same generated text. That was true of the schema and not of the
    /// sentence beside it, which was a copy — and a copy is exactly what that comment promised
    /// there was none of.
    /// </para>
    /// <para>
    /// Joined with an explicit newline for the reason <c>MedicalPromptBlocks.NL</c> gives: a raw
    /// string literal carries the line endings of the file it is written in, which differ between
    /// a Windows checkout and the runner that builds what serves members.
    /// </para>
    /// </remarks>
    internal static string PromptWithSchema<T>(string prompt) => PromptWithSchema(prompt, TextFor<T>());

    /// <inheritdoc cref="PromptWithSchema{T}"/>
    internal static string PromptWithSchema(string prompt, string schemaText) =>
        prompt + "\n\n"
        + "Respond with ONLY a single JSON object that satisfies this JSON Schema,"
        + " with no fields beyond what it defines:\n"
        + schemaText;
}
