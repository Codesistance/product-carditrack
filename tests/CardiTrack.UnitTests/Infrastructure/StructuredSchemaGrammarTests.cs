using System.Reflection;
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
/// field fails here instead of on the first live call.
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

    private static string SchemaTextFor(Type type) =>
        (string)typeof(MedGemmaClient)
            .GetMethod("SchemaTextFor", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)!
            .MakeGenericMethod(type)
            .Invoke(null, null)!;
}
