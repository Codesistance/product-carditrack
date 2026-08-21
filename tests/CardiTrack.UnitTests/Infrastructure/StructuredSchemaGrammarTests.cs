using System.Reflection;
using CardiTrack.Infrastructure.ExternalClients;
using CardiTrack.Infrastructure.ExternalClients.Medical;

namespace CardiTrack.UnitTests.Infrastructure;

/// <summary>
/// The JSON Schema <see cref="MedGemmaClient"/> generates for a structured call is compiled by
/// llama.cpp into a decoding grammar, and that compiler is far stricter than JSON Schema: a
/// <c>pattern</c> regex using <c>\d</c> fails with "failed to parse grammar" and Ollama answers
/// 400 for every call with that type — which is how member chat's query-plan step failed on every
/// send (2026-08-20): the Web serializer default (numbers-from-strings) made the exporter render
/// every numeric property as string-or-integer plus a <c>\d</c> pattern. These tests pin the
/// invariant on the real generator for every AI response record, so the next type with a numeric
/// field fails here instead of on the first live call. The later tests widen that to the rest
/// of what the schema decides: what the grammar permits at the root, and how the field
/// descriptions read once they reach the model as prompt text.
/// </summary>
public class StructuredSchemaGrammarTests
{
    /// <summary>Every internal record the services hand to a structured generate call, found by
    /// the naming convention they all follow.</summary>
    public static TheoryData<Type> AiResponseTypes()
    {
        var data = new TheoryData<Type>();
        var types = typeof(MedGemmaClient).Assembly.GetTypes()
            .Where(t => t.Name.EndsWith("AiResponse", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(types);
        foreach (var type in types)
            data.Add(type);
        return data;
    }

    [Theory]
    [MemberData(nameof(AiResponseTypes))]
    public void Schema_ContainsNoRegexPattern(Type aiResponseType)
    {
        var schemaText = SchemaTextFor(aiResponseType);

        Assert.DoesNotContain("\"pattern\"", schemaText);
        Assert.DoesNotContain("\\d", schemaText);
    }

    [Fact]
    public void NumericProperties_ExportAsPlainIntegers_NotStringOrInteger()
    {
        // The concrete case that failed live: DataQueryPlanAiResponse's two nullable ints.
        var schemaText = SchemaTextFor(
            typeof(CardiTrack.Infrastructure.Services.DataQueryPlannerService.DataQueryPlanAiResponse));

        Assert.Contains("\"recentActivityDays\":{\"type\":[\"integer\",\"null\"]}", schemaText);
        Assert.DoesNotContain("\"string\",\"integer\"", schemaText);
    }

    /// <summary>
    /// The reply is an object. Every AI response record is a reference type and the exporter has
    /// no root nullability annotation to read, so it used to export
    /// <c>{"type":["object","null"], …}</c> for all of them — and both providers compile the schema
    /// into a decoding grammar, which made a bare <c>null</c> a legal reply and a legal first
    /// token. It is also the one reply neither client can use: null deserialises to null and
    /// becomes a failed generation. The constraint that exists to stop unusable output was
    /// offering the unusable answer as a branch.
    /// </summary>
    [Theory]
    [MemberData(nameof(AiResponseTypes))]
    public void Schema_DeclaresAnObjectAtTheRoot_NotAnObjectOrNull(Type aiResponseType)
    {
        var schemaText = SchemaTextFor(aiResponseType);

        Assert.StartsWith("{\"type\":\"object\"", schemaText, StringComparison.Ordinal);
        Assert.DoesNotContain("\"type\":[\"object\",\"null\"]", schemaText);
    }

    /// <summary>
    /// Only the root loses its null. A nullable property is a real part of the contract — a
    /// daybook with no suggestion worth making says so with a null — and the schema has to keep
    /// letting it.
    /// </summary>
    [Fact]
    public void Schema_KeepsNullOnAPropertyThatIsGenuinelyOptional()
    {
        var schemaText = SchemaTextFor(
            typeof(CardiTrack.Infrastructure.Services.DigestGenerationService.DaybookAiResponse));

        Assert.Contains("\"suggestion\":{\"type\":[\"string\",\"null\"]", schemaText);
    }

    /// <summary>
    /// The schema is not only a wire contract — its text is appended to the prompt, so the field
    /// descriptions are read by a model as English. The default encoder escaped every non-ASCII
    /// character and the apostrophe to a <c>\uXXXX</c> sequence, and those survived all the way
    /// into the prompt: the description of a daybook's summary reached the model reading
    /// "CardiTrackCardiMember's whole day".
    /// </summary>
    [Theory]
    [MemberData(nameof(AiResponseTypes))]
    public void Schema_ReachesTheModelAsText_NotAsEscapeSequences(Type aiResponseType)
    {
        Assert.DoesNotContain("\\u", SchemaTextFor(aiResponseType), StringComparison.Ordinal);
    }

    /// <summary>
    /// Both clients used to write this sentence out in full, each directly beneath a comment
    /// explaining that the schema and the prompt's description of it cannot drift because both
    /// come from the same generated text. That held for the schema and not for the sentence.
    /// </summary>
    [Fact]
    public void PromptWithSchema_PutsTheBriefFirstAndTheSchemaLast_SeparatedByOneInstruction()
    {
        var composed = StructuredOutputSchema.PromptWithSchema("THE BRIEF", "THE SCHEMA");

        Assert.Equal(
            "THE BRIEF\n\nRespond with ONLY a single JSON object that satisfies this JSON Schema,"
            + " with no fields beyond what it defines:\nTHE SCHEMA",
            composed);
        Assert.DoesNotContain('\r', composed);
    }

    private static string SchemaTextFor(Type type) => StructuredOutputSchema.TextFor(type);
}
