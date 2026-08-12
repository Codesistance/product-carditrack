using System.Reflection;
using CardiTrack.Infrastructure.Services;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// One voice across every generation a caregiver reads. These pin the shared tone block itself
/// rather than any one caller: the failure this guards against is a prompt being added later that
/// quietly speaks in its own register, which nothing else in the suite would notice.
/// </summary>
public class MedicalPromptToneTests
{
    /// <summary>
    /// Every fixed instruction block in the private-model services. Found by reflection rather than
    /// listed, so a prompt added tomorrow is held to this without anyone remembering to add it here.
    /// </summary>
    public static TheoryData<string, string> Prompts()
    {
        var data = new TheoryData<string, string>();

        foreach (var type in new[]
                 {
                     typeof(DigestGenerationService),
                     typeof(HealthInsightService),
                     typeof(RealtimeAssessmentService),
                 })
        {
            var fields = type
                .GetFields(BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(f => f.IsLiteral && f.FieldType == typeof(string))
                .Where(f => f.Name.EndsWith("Instructions", StringComparison.Ordinal));

            foreach (var field in fields)
                data.Add($"{type.Name}.{field.Name}", (string)field.GetRawConstantValue()!);
        }

        return data;
    }

    [Fact]
    public void Every_service_contributes_at_least_one_prompt()
    {
        // Guards the reflection above: a rename that stopped matching "…Instructions" would
        // otherwise turn every test below into a silent pass over an empty set.
        Assert.Equal(7, Prompts().Count);
    }

    [Theory]
    [MemberData(nameof(Prompts))]
    public void Every_prompt_opens_with_the_shared_tone(string name, string prompt)
    {
        // First, not merely present: these blocks are the cacheable fixed prefix the serving engine
        // reuses between calls, and a shared opening is what makes that prefix shared.
        Assert.True(
            prompt.StartsWith(MedicalPromptBlocks.Tone, StringComparison.Ordinal),
            $"{name} does not open with the shared tone block.");
    }

    /// <summary>
    /// The tone must not be one-sided. "Be reassuring" alone would be an unsafe brief for the
    /// prompts behind an alerting service — a model told only to soothe softens the one reading
    /// that needed saying plainly — so the block constrains distortion in both directions.
    /// </summary>
    [Fact]
    public void The_tone_forbids_understating_as_well_as_overstating()
    {
        Assert.Contains("no urgency the data does not carry", MedicalPromptBlocks.Tone);
        Assert.Contains("no reassurance it does not support", MedicalPromptBlocks.Tone);
    }

    [Fact]
    public void The_tone_never_blames_the_family()
    {
        Assert.Contains("Never suggest the family has missed something", MedicalPromptBlocks.Tone);
    }

    /// <summary>
    /// Each rule in the shared block sits wholly on one line. The echo guards that stop a model's
    /// own brief being shown to a caregiver match phrases against the reply, so a rule split
    /// across two lines is one that can never be matched whole — the guard would go on passing
    /// while catching nothing, which is the worst way for a safety check to fail.
    /// </summary>
    [Fact]
    public void Every_rule_in_the_shared_tone_fits_on_one_line()
    {
        var lines = MedicalPromptBlocks.Tone
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

        Assert.All(lines, line => Assert.EndsWith(".", line));
        Assert.Equal(7, lines.Count);
    }

    [Theory]
    [MemberData(nameof(Prompts))]
    public void No_prompt_still_carries_its_own_never_alarm_rule(string _, string prompt)
    {
        // The blanket "Never alarm" the digest prompt used to end on is exactly the instruction the
        // two-sided tone block replaces. One of them saying it and the other qualifying it would
        // leave the model to decide which it meant.
        Assert.DoesNotContain("Never alarm", prompt, StringComparison.OrdinalIgnoreCase);
    }
}
