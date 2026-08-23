using System.Text.Json;
using System.Text.RegularExpressions;
using CardiTrack.Application.DTOs.Common;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Enums;

namespace CardiTrack.UnitTests.Architecture;

/// <summary>
/// <see cref="ChatWorkflowCatalogue"/> is the vocabulary the member-chat router is grounded in and
/// the rule its output is validated against — one object serving both, so the prompt and the
/// validator cannot describe different sets. That only holds while the catalogue agrees with the
/// persisted <see cref="MemberChatWorkflow"/> enum and with its own claim rules, which is what
/// these assert.
/// </summary>
/// <remarks>
/// The handler half of the parity check — every rendered id has a registered handler, and every
/// handler is either routable or explicitly app-triggered — lands when the handlers become types.
/// Today they are private methods on <c>MemberChatService</c> selected by the triage chain, so
/// there is nothing to reflect over. What is assertable now is the vocabulary and the claim
/// limits, and those are the parts a new entry gets wrong first.
/// </remarks>
public class ChatWorkflowCatalogueParityTests
{
    /// <summary>Ids retired from the catalogue but kept in the enum so turns already stamped with
    /// them keep their meaning.</summary>
    private static readonly MemberChatWorkflow[] Retired = [MemberChatWorkflow.Steer];

    [Fact]
    public void EveryLiveEnumMember_HasACatalogueEntry()
    {
        var missing = Enum.GetValues<MemberChatWorkflow>()
            .Except(Retired)
            .Where(id => ChatWorkflowCatalogue.Find(id) is null)
            .ToList();

        Assert.True(missing.Count == 0,
            $"MemberChatWorkflow member(s) with no catalogue entry: {string.Join(", ", missing)}. " +
            "An id the router or the app can produce but the catalogue does not describe has no " +
            "purpose line, no claim class and no allowed datasets — it would route to nothing. Add " +
            "the entry alongside the enum member, or add the id to Retired if it exists only so " +
            "already-stored turns keep their meaning.");
    }

    [Fact]
    public void EveryCatalogueEntry_NamesADefinedEnumMember()
    {
        var orphans = ChatWorkflowCatalogue.All
            .Where(w => !Enum.IsDefined(w.Id))
            .Select(w => w.Id)
            .ToList();

        Assert.True(orphans.Count == 0,
            $"Catalogue entr(y/ies) naming an undefined enum value: {string.Join(", ", orphans)}.");
    }

    [Fact]
    public void NoEntryIsListedTwice()
    {
        var duplicates = ChatWorkflowCatalogue.All
            .GroupBy(w => w.Id)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.True(duplicates.Count == 0,
            $"Duplicate catalogue entr(y/ies): {string.Join(", ", duplicates)}. Find() returns the " +
            "first match, so a duplicate silently shadows whichever definition was written second.");
    }

    /// <summary>
    /// The rule the whole claim-class field exists for. Only the entry that serves a suggestion
    /// grounded ahead of time in a public-health reference may recommend anything; every chat
    /// prompt carries ToneNoDiagnosis instead.
    /// </summary>
    [Fact]
    public void OnlyAdvise_MayClaimSuggestion()
    {
        var suggesting = ChatWorkflowCatalogue.All
            .Where(w => w.ClaimClass == ChatClaimClass.Suggestion)
            .Select(w => w.Id)
            .ToList();

        Assert.Equal([MemberChatWorkflow.Advise], suggesting);
    }

    /// <summary>
    /// A workflow that recommends or says nothing about the member has no business fetching their
    /// readings. Advise serves a stored row; the steers and clarify touch no data at all.
    /// </summary>
    [Fact]
    public void EntriesThatDoNotCompare_FetchNothing()
    {
        var offenders = ChatWorkflowCatalogue.All
            .Where(w => w.ClaimClass is ChatClaimClass.Suggestion or ChatClaimClass.None)
            .Where(w => w.AllowedDatasets.Count > 0)
            .Select(w => $"{w.Id} ({w.ClaimClass})")
            .ToList();

        Assert.True(offenders.Count == 0,
            $"Entr(y/ies) claiming {nameof(ChatClaimClass.Suggestion)} or {nameof(ChatClaimClass.None)} " +
            $"while declaring datasets: {string.Join(", ", offenders)}. Advise serves a stored, " +
            "already-grounded row and the steers answer nothing about the member — a fetch here is " +
            "either dead weight or the beginning of a per-question suggestion, which is the one " +
            "thing the batch-generation split exists to prevent.");
    }

    /// <summary>
    /// A judgement rests on a comparison, so an entry licensed to judge must be able to reach the
    /// baseline it would compare against.
    /// </summary>
    [Fact]
    public void JudgingEntries_CanReachTheBaseline()
    {
        var ungrounded = ChatWorkflowCatalogue.All
            .Where(w => w.ClaimClass == ChatClaimClass.Judgement)
            .Where(w => !w.AllowedDatasets.Contains(DataQueryKind.Baseline))
            .Select(w => w.Id)
            .ToList();

        Assert.True(ungrounded.Count == 0,
            $"Entr(y/ies) licensed to judge without access to the baseline: {string.Join(", ", ungrounded)}. " +
            "A judgement must name what it rests on — the published band or the member's own range — " +
            "and one that cannot reach the baseline cannot do the second.");
    }

    [Fact]
    public void ClarifyIsNeverRoutable()
    {
        var clarify = ChatWorkflowCatalogue.Find(MemberChatWorkflow.Clarify);

        Assert.NotNull(clarify);
        Assert.False(clarify.IsRoutable,
            "Clarify is triggered by the shape of the routing answer — a non-adjacent runner-up, or " +
            "a result that cannot be run — and is never returned by the model. Rendering it into the " +
            "prompt would let the router choose to ask a question instead of answering one.");
    }

    [Fact]
    public void NothingUnimplementedOrUnroutable_IsRendered()
    {
        var wrong = ChatWorkflowCatalogue.Routable
            .Where(w => !w.IsImplemented || !w.IsRoutable)
            .Select(w => w.Id)
            .ToList();

        Assert.True(wrong.Count == 0,
            $"Routable renders entr(y/ies) it should not: {string.Join(", ", wrong)}. The routing " +
            "prompt must offer only ids that something can actually run.");
    }

    [Fact]
    public void EveryRenderedEntry_HasANonEmptyPurposeLine()
    {
        var blank = ChatWorkflowCatalogue.Routable
            .Where(w => string.IsNullOrWhiteSpace(w.Purpose))
            .Select(w => w.Id)
            .ToList();

        Assert.True(blank.Count == 0,
            $"Entr(y/ies) rendered with no purpose line: {string.Join(", ", blank)}. The purpose " +
            "lines are the routing prompt; a blank one is an id the model is asked to choose with " +
            "nothing to choose it on.");
    }

    // ---------------------------------------------------------------------------------------
    // Labels, and the eval fixture that is written in them
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The bundled eval fixture — <c>tools/ChatRoutingEval/cases.json</c>, linked into this project
    /// rather than copied, so these assert against the file the tool actually ships.
    /// </summary>
    private static CaseFile Cases
    {
        get
        {
            var path = Path.Combine(AppContext.BaseDirectory, "cases.json");
            Assert.True(File.Exists(path),
                $"No eval fixture at {path}. It is linked from tools/ChatRoutingEval/cases.json by " +
                "this project's csproj; a missing file means that link was dropped, not that the " +
                "fixture is optional.");

            return JsonSerializer.Deserialize<CaseFile>(
                       File.ReadAllText(path),
                       new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                   ?? throw new InvalidOperationException($"{path} did not parse as a case fixture.");
        }
    }

    /// <summary>Outcomes a labeller may record that are not catalogue entries — kept in step with
    /// <c>Vocabulary.Special</c> in the eval tool.</summary>
    private static readonly string[] NonCatalogueOutcomes = ["inherit-prior", "rejected-pre-router"];

    private static IReadOnlyCollection<string> Vocabulary { get; } =
        ChatWorkflowCatalogue.All.Select(w => w.Label).Concat(NonCatalogueOutcomes).ToHashSet();

    [Fact]
    public void EveryEntry_HasAUsableLabel()
    {
        // A label is a required constructor parameter, so "missing" is already a compile error.
        // What the compiler cannot refuse is an empty or badly-shaped one, and the label travels
        // into prompts and onto labelling sheets where casing and stray whitespace matter.
        var bad = ChatWorkflowCatalogue.All
            .Where(w => !Regex.IsMatch(w.Label, "^[a-z]+([.-][a-z]+)*$"))
            .Select(w => $"{w.Id}='{w.Label}'")
            .ToList();

        Assert.True(bad.Count == 0,
            $"Entr(y/ies) with an unusable label: {string.Join(", ", bad)}. A label is the entry's " +
            "external name — it goes into the routing prompt and onto labelling sheets that outlive " +
            "any one run — so it must be lowercase words joined by dots or hyphens, no spaces.");
    }

    [Fact]
    public void NoLabelIsUsedTwice()
    {
        var duplicates = ChatWorkflowCatalogue.All
            .GroupBy(w => w.Label)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.True(duplicates.Count == 0,
            $"Label(s) on more than one entry: {string.Join(", ", duplicates)}. The router returns " +
            "a label, so two entries sharing one makes the answer ambiguous and silently picks " +
            "whichever entry is found first.");
    }

    [Fact]
    public void NoLabelCollidesWithANonCatalogueOutcome()
    {
        var collisions = ChatWorkflowCatalogue.All
            .Where(w => NonCatalogueOutcomes.Contains(w.Label))
            .Select(w => w.Label)
            .ToList();

        Assert.True(collisions.Count == 0,
            $"Label(s) colliding with a non-catalogue labelling outcome: {string.Join(", ", collisions)}. " +
            "A labeller writing that word would be recorded as choosing the workflow, not the outcome.");
    }

    [Fact]
    public void EveryEvalCase_ExpectsALabelInTheVocabulary()
    {
        var unknown = Cases.Cases
            .SelectMany(c => c.Special is null ? c.Accepted : [c.Special])
            .Where(label => !Vocabulary.Contains(label))
            .Distinct()
            .ToList();

        Assert.True(unknown.Count == 0,
            $"Eval case(s) expecting label(s) outside the vocabulary: {string.Join(", ", unknown)}. " +
            "Renaming a workflow invalidates the answer key without touching the fixture, and the " +
            "symptom is every labeller scoring wrong on those cases — which reads as a labelling " +
            "failure rather than the fixture bug it is. Update tools/ChatRoutingEval/cases.json.");
    }

    [Fact]
    public void NoEvalCaseIdIsUsedTwice()
    {
        var duplicates = Cases.Cases
            .GroupBy(c => c.Id)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.True(duplicates.Count == 0,
            $"Eval case id(s) listed twice: {string.Join(", ", duplicates)}. Scoring keys labels by " +
            "case id, so a duplicate makes one of the two unreachable.");
    }

    private sealed record CaseFile(EvalCase[] Cases);

    private sealed record EvalCase(string Id, string[] Accepted, string? Special);
}
