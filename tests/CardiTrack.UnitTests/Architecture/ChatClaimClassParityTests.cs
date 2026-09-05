using CardiTrack.Application.Services;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Services;

namespace CardiTrack.UnitTests.Architecture;

/// <summary>
/// The claim limit, asserted against the briefs the handlers actually send.
/// </summary>
/// <remarks>
/// <para>
/// <c>docs/technical/member_chat_routing.md</c> §5 states that "the parity test asserts each
/// handler's prompt carries the tone block matching its claim class". It did not.
/// <see cref="ChatWorkflowCatalogueParityTests"/>' assertions all evaluate the catalogue array —
/// which entry may claim what, which may fetch what — and <see cref="ChatClaimClass"/> was read by
/// no runtime code, no prompt, and no other test. Its neighbour <c>AllowedDatasets</c> is enforced
/// at runtime, passed to the planner and intersected in the parse; the claim limit had nothing.
/// </para>
/// <para>
/// The catalogue calls <c>claimClass</c> "the load-bearing field… the only place that limit is
/// written down". Written down is where it stopped. These are the teeth.
/// </para>
/// <para>
/// Scope: the <em>clinical</em> brief. The rewrite step shares one set of instructions across every
/// generated rung, so §5's separate claim that analysis gets its own rewrite brief "that states
/// figures and direction warmly and stops short of whether that is good" is still unbuilt — a
/// documented gap, not one these assertions can paper over.
/// </para>
/// </remarks>
public class ChatClaimClassParityTests
{
    private static IEnumerable<ChatWorkflowDefinition> Implemented =>
        ChatWorkflowCatalogue.All.Where(w => w.IsImplemented && w.Id != MemberChatWorkflow.Steer);

    /// <summary>
    /// Every implemented rung either sends a brief or is one of the three that assemble their reply
    /// in code. A new rung that shipped with neither would be a handler nobody briefed, and the map
    /// is what makes that visible instead of plausible.
    /// </summary>
    [Fact]
    public void EveryImplementedRung_EitherSendsABrief_OrIsAssembledInCode()
    {
        var unaccounted = Implemented
            .Where(w => !MemberChatService.HandlerBriefs.ContainsKey(w.Id)
                        && !MemberChatService.CodeAssembledRungs.Contains(w.Id))
            .Select(w => w.Label)
            .ToList();

        Assert.True(unaccounted.Count == 0,
            $"Rung(s) with no brief and not listed as code-assembled: {string.Join(", ", unaccounted)}. "
            + "A rung reaches a model with a brief or it does not reach one at all; a third state is "
            + "a handler nobody wrote instructions for.");
    }

    [Fact]
    public void NothingIsBothBriefedAndCodeAssembled()
    {
        var both = MemberChatService.HandlerBriefs.Keys
            .Where(MemberChatService.CodeAssembledRungs.Contains)
            .ToList();

        Assert.True(both.Count == 0, $"Rung(s) claiming to be both: {string.Join(", ", both)}.");
    }

    /// <summary>
    /// <b>The load-bearing one.</b> <c>ToneWellness</c> is the only permission on this platform to
    /// suggest anything, and it belongs to <c>AdviseGenerationService</c> alone — which earns it
    /// with machinery no per-question path reproduces inside a caregiver's wait: the suggestion is
    /// grounded in a published guideline and the model must name which, so an ungrounded reply is
    /// one the code can recognise and withhold.
    /// <para>
    /// The catalogue already asserts that only <c>advise</c> may claim
    /// <see cref="ChatClaimClass.Suggestion"/>. That is a statement about a table. This is the same
    /// statement about the text actually sent to a model, which is where it would be violated.
    /// </para>
    /// </summary>
    [Fact]
    public void NoChatBriefCarriesTheSuggestionLicence()
    {
        foreach (var (workflow, brief) in MemberChatService.HandlerBriefs)
        {
            Assert.False(brief.Contains(MedicalPromptBlocks.ToneWellnessNotClinical, StringComparison.Ordinal),
                $"The {workflow} brief carries ToneWellness. Chat may not compose a suggestion: "
                + "AdviseGenerationService is the only prompt licensed to, and it earns that with "
                + "guideline grounding this path has no way to reproduce.");
        }
    }

    /// <summary>
    /// A rung licensed to compare or to judge reads clinically, and says so in its brief. The
    /// <c>ClinicalRead</c> block is what tells the model it is writing for another step rather than
    /// for a family — the premise the whole two-slot split rests on.
    /// </summary>
    [Fact]
    public void EveryComparingOrJudgingRung_ReadsClinically()
    {
        var expected = Implemented
            .Where(w => w.ClaimClass is ChatClaimClass.Comparison or ChatClaimClass.Judgement)
            .Select(w => w.Id);

        foreach (var id in expected)
        {
            Assert.True(MemberChatService.HandlerBriefs.TryGetValue(id, out var brief),
                $"{id} may compare or judge but sends no brief — it cannot be doing either in code.");
            Assert.Contains(MedicalPromptBlocks.ClinicalRead, brief, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A rung that says nothing about the member never reads clinically. The steers answer "hi" and
    /// "what's the weather"; a clinical brief on that path would widen what the slot sees for no
    /// gain, which is the opposite of the minimisation DPIA row A20 records for it.
    /// </summary>
    [Fact]
    public void ARungThatSaysNothingAboutTheMember_NeverReadsClinically()
    {
        var sayNothing = Implemented
            .Where(w => w.ClaimClass is ChatClaimClass.None)
            .Select(w => w.Id)
            .Where(MemberChatService.HandlerBriefs.ContainsKey);

        foreach (var id in sayNothing)
        {
            Assert.DoesNotContain(
                MedicalPromptBlocks.ClinicalRead, MemberChatService.HandlerBriefs[id], StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Every brief that reaches a model with the caregiver's own words in it carries the guardrail
    /// naming them as words to act on rather than instructions to follow.
    /// </summary>
    [Fact]
    public void EveryBriefGuardsTheCaregiversWords()
    {
        foreach (var (workflow, brief) in MemberChatService.HandlerBriefs)
        {
            Assert.True(
                brief.Contains(MedicalPromptBlocks.ChatMessageGuardrail, StringComparison.Ordinal)
                || brief.Contains(MedicalPromptBlocks.ChatQuestionGuardrail, StringComparison.Ordinal),
                $"The {workflow} brief renders the caregiver's message with no untrusted framing.");
        }
    }

    /// <summary>
    /// The reading rungs are told the data is finished, never live. This is the rule that failed in
    /// production — asked "is he asleep now?" the clinical read answered "Yes, Dad is asleep now"
    /// from a nightly total — and the reason status answers that question in code instead. The
    /// prompt rule stays anyway: it is the second line of defence for every phrasing the triage
    /// does not catch.
    /// </summary>
    [Fact]
    public void EveryClinicalBriefSaysTheDataIsAlreadyFinished()
    {
        var clinical = Implemented
            .Where(w => w.ClaimClass is ChatClaimClass.Comparison or ChatClaimClass.Judgement)
            .Select(w => w.Id)
            .Where(MemberChatService.HandlerBriefs.ContainsKey);

        foreach (var id in clinical)
        {
            var brief = MemberChatService.HandlerBriefs[id];
            Assert.True(
                brief.Contains("dates named in", StringComparison.OrdinalIgnoreCase)
                || brief.Contains("headings", StringComparison.OrdinalIgnoreCase),
                $"The {id} brief never tells the model which dates its data covers, so an answer "
                + "can silently span days it was not given.");
        }
    }
}
