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

        Assert.Contains("\"recentActivityDays\":{\"type\":[\"integer\",\"null\"]}", schemaText);
        Assert.DoesNotContain("\"string\",\"integer\"", schemaText);
    }

    private static string SchemaTextFor(Type type) =>
        (string)typeof(MedGemmaClient)
            .GetMethod("SchemaTextFor", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)!
            .MakeGenericMethod(type)
            .Invoke(null, null)!;
}
