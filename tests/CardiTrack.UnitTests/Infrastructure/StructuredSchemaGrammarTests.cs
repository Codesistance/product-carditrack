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

        // Matched without a closing brace on purpose: the property node carries a description
        // alongside its type, and pinning the whole node would break on any wording change while
        // proving nothing more about the type — which is the only thing llama.cpp's grammar
        // compiler cares about here.
        Assert.Contains("\"recentActivityDays\":{\"type\":[\"integer\",\"null\"]", schemaText);
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
    /// Array items lose their null too — the exporter has no nullability view of a collection's
    /// reference-type items, so a list of entry records exported as items that may each be null,
    /// and the grammar then offered a null element no deserializer wanted. Pinned on the first
    /// response type with an object list, so the array half of the stripping cannot silently
    /// regress even if that type leaves the theory's roster.
    /// </summary>
    [Fact]
    public void Schema_DeclaresObjectsInsideArrays_NotObjectOrNull()
    {
        var schemaText = SchemaTextFor(
            typeof(CardiTrack.Infrastructure.Services.AdviseGenerationService.AdviseClinicalAiResponse));

        Assert.Contains("\"items\":{\"type\":\"object\"", schemaText);
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

    /// The query planner's <c>metrics</c> field decides which charts a caregiver is shown, and it
    /// used to be optional: a model that simply omitted it was indistinguishable from one saying
    /// "this question is general", and both widen to every chart. That is how "show me his steps
    /// this week" came back with steps, heart rate and sleep. Marking it required is the fix, so
    /// the schema is where it has to be proven — the prompt can ask nicely, the schema compels.
    /// </summary>
    [Fact]
    public void TheQueryPlanner_MustAnswerWhichMetricsAQuestionIsAbout()
    {
        var schemaText = SchemaTextFor(
            typeof(CardiTrack.Infrastructure.Services.DataQueryPlannerService.DataQueryPlanAiResponse));

        Assert.Contains("\"required\"", schemaText);
        Assert.Contains("\"metrics\"", schemaText);

        var required = schemaText[schemaText.IndexOf("\"required\"", StringComparison.Ordinal)..];
        Assert.Contains("\"metrics\"", required);
        Assert.Contains("\"sources\"", required);
    }

    /// <summary>
    /// Descriptions are the only account of a field's meaning the model gets from the schema
    /// itself. The planner shipped without any, which left "metrics" explained by the word
    /// "metrics".
    /// </summary>
    [Fact]
    public void TheQueryPlannersFields_CarryTheirDescriptionsIntoTheSchema()
    {
        var schemaText = SchemaTextFor(
            typeof(CardiTrack.Infrastructure.Services.DataQueryPlannerService.DataQueryPlanAiResponse));

        Assert.Contains("Steps, RestingHeartRate, Sleep", schemaText);
        Assert.Contains("\"description\"", schemaText);
    }

    private static string SchemaTextFor(Type type) => StructuredOutputSchema.TextFor(type);

}
