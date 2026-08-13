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
    /// <summary>The services that send prompts to the private medical model.</summary>
    private static readonly Type[] PromptServices =
    [
        typeof(DigestGenerationService),
        typeof(HealthInsightService),
        typeof(RealtimeAssessmentService),
    ];

    /// <summary>
    /// Every fixed instruction block in those services. Found by reflection rather than listed, so
    /// a prompt added tomorrow is held to the rules below without anyone remembering to add it
    /// here — which is also why nothing asserts on how many there are.
    /// </summary>
    private static List<(string Service, string Field, string Prompt)> AllPrompts() =>
        PromptServices
            .SelectMany(type => type
                .GetFields(BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(f => f.IsLiteral && f.FieldType == typeof(string))
                .Where(f => f.Name.EndsWith("Instructions", StringComparison.Ordinal))
                .Select(f => (type.Name, f.Name, (string)f.GetRawConstantValue()!)))
            .ToList();

    public static TheoryData<string, string> Prompts()
    {
        var data = new TheoryData<string, string>();
        foreach (var (service, field, prompt) in AllPrompts())
            data.Add($"{service}.{field}", prompt);
        return data;
    }

    /// <summary>
    /// Guards the reflection above. A rename that stopped matching "…Instructions" would otherwise
    /// turn every test below into a silent pass over an empty set — so this asserts each service
    /// is actually represented, rather than a total that would need editing every time a prompt
    /// was legitimately added.
    /// </summary>
    [Fact]
    public void Every_service_contributes_at_least_one_prompt()
    {
        var found = AllPrompts().Select(p => p.Service).ToHashSet(StringComparer.Ordinal);

        Assert.All(PromptServices, type => Assert.True(
            found.Contains(type.Name),
            $"{type.Name} contributed no '…Instructions' constant — either it stopped sending "
            + "prompts, or the reflection here has stopped finding them and the tone rules below "
            + "are no longer checking anything."));
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
        Assert.Equal(5, lines.Count);
    }

    /// <summary>The one prompt the pronoun rule is deliberately kept out of.</summary>
    private const string StatusPrompt = "HealthInsightService.CurrentStatusInstructions";

    /// <summary>
    /// Anything that writes more than a sentence gets the pronoun rule. Without it the model
    /// repeats the <c>{{NAME}}</c> placeholder in every sentence it writes, which reads as a case
    /// file rather than as the voice the tone block asks for.
    /// </summary>
    [Theory]
    [MemberData(nameof(Prompts))]
    public void Every_prose_prompt_carries_the_pronoun_rule(string name, string prompt)
    {
        if (name == StatusPrompt)
            return;

        Assert.Contains(MedicalPromptBlocks.Pronouns.Trim(), prompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// The status prompt is the exception, and stays one. It asks for a headline of two to five
    /// words and a sentence under twelve — a pronoun scarcely arises, and its own instructions
    /// already settle how the person is named. It is also the only prompt a caregiver waits on and
    /// the only one under a character budget, so an inert rule here is paid for in latency on
    /// nearly every dashboard view. Deleting this test is the cheap way to lose that.
    /// </summary>
    [Fact]
    public void The_status_prompt_is_left_out_of_the_pronoun_rule()
    {
        var status = AllPrompts().Single(p => $"{p.Service}.{p.Field}" == StatusPrompt).Prompt;

        Assert.DoesNotContain(MedicalPromptBlocks.Pronouns.Trim(), status, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every member created before M1-04 asked for sex sits at "not stated". A rule that named no
    /// fallback would leave a model that has been told to use a pronoun to infer one from an age
    /// and a set of readings, which it will do.
    /// </summary>
    [Fact]
    public void The_pronoun_rule_says_what_to_do_when_sex_is_not_stated()
    {
        Assert.Contains("they if it is not stated", MedicalPromptBlocks.Pronouns);
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
