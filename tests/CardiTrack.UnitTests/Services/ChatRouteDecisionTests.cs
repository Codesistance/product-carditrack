using CardiTrack.Application.DTOs.Common;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Services;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// The routing decision's pure half: label parsing against the catalogue, the ladder's neighbour
/// relation, and when clarify fires. These are the rules §5 of the design states in prose; a
/// router whose model calls are mocked out is exactly these rules.
/// </summary>
public class ChatRouteDecisionTests
{
    // ---- parsing --------------------------------------------------------------------------

    [Theory]
    [InlineData("status", MemberChatWorkflow.Status)]
    [InlineData("analysis", MemberChatWorkflow.Analysis)]
    [InlineData("inference", MemberChatWorkflow.Inference)]
    [InlineData("advise", MemberChatWorkflow.Advise)]
    [InlineData("steer.casual", MemberChatWorkflow.SteerCasual)]
    [InlineData("steer.offtopic", MemberChatWorkflow.SteerOffTopic)]
    [InlineData("  Analysis  ", MemberChatWorkflow.Analysis)]
    public void ParseLabel_MapsRoutableLabels(string label, MemberChatWorkflow expected) =>
        Assert.Equal(expected, ChatRouteDecision.ParseLabel(label));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("weather")]
    [InlineData("clarify")]        // unroutable: the router may never return it
    [InlineData("2")]              // numerics never coerce — the closed-vocabulary rule
    public void ParseLabel_DropsWhatTheRouterMayNotReturn(string? label) =>
        Assert.Null(ChatRouteDecision.ParseLabel(label));

    [Fact]
    public void ParseLabel_DropsInvestigation_UntilItsHandlerShips()
    {
        // investigation is IsImplemented:false today, so it does not render and must not parse —
        // this test flips its assertion the day the handler ships and the flag turns.
        var expected = CardiTrack.Application.Services.ChatWorkflowCatalogue
            .Routable.Any(w => w.Id == MemberChatWorkflow.Investigation)
            ? (MemberChatWorkflow?)MemberChatWorkflow.Investigation
            : null;

        Assert.Equal(expected, ChatRouteDecision.ParseLabel("investigation"));
    }

    // ---- the failure direction ------------------------------------------------------------

    [Fact]
    public void AnUnparseablePrimary_LeavesPrimaryNull_SoTheCallerDescendsToAnalysis()
    {
        var decision = new ChatRouteDecision { Primary = ChatRouteDecision.ParseLabel("nonsense") };

        Assert.Null(decision.Primary);
        Assert.False(decision.NeedsClarify);
    }

    // ---- adjacency and clarify ------------------------------------------------------------

    [Theory]
    [InlineData(MemberChatWorkflow.Analysis, MemberChatWorkflow.Inference, false)]  // the superset pair
    [InlineData(MemberChatWorkflow.Status, MemberChatWorkflow.Analysis, false)]
    [InlineData(MemberChatWorkflow.Inference, MemberChatWorkflow.Investigation, false)]
    [InlineData(MemberChatWorkflow.Investigation, MemberChatWorkflow.Advise, false)]
    [InlineData(MemberChatWorkflow.SteerCasual, MemberChatWorkflow.SteerOffTopic, false)]  // both off-ladder
    [InlineData(MemberChatWorkflow.Status, MemberChatWorkflow.Advise, true)]     // §5's own example
    [InlineData(MemberChatWorkflow.SteerCasual, MemberChatWorkflow.Analysis, true)]  // §5's other example
    [InlineData(MemberChatWorkflow.SteerOffTopic, MemberChatWorkflow.Analysis, true)]
    [InlineData(MemberChatWorkflow.Status, MemberChatWorkflow.Inference, true)]
    [InlineData(MemberChatWorkflow.Analysis, MemberChatWorkflow.Advise, true)]
    public void Clarify_FiresOnNonAdjacentPairs_AndOnlyThose(
        MemberChatWorkflow primary, MemberChatWorkflow runnerUp, bool expectClarify)
    {
        var decision = new ChatRouteDecision { Primary = primary, RunnerUp = runnerUp };

        Assert.Equal(expectClarify, decision.NeedsClarify);
        // Symmetric: which of the two the model happened to put first must not change the answer.
        Assert.Equal(expectClarify,
            new ChatRouteDecision { Primary = runnerUp, RunnerUp = primary }.NeedsClarify);
    }

    [Fact]
    public void NoRunnerUp_NeverClarifies()
    {
        var decision = new ChatRouteDecision { Primary = MemberChatWorkflow.Analysis };
        Assert.False(decision.NeedsClarify);
    }

    [Fact]
    public void ARunnerUpEqualToThePrimary_NeverClarifies()
    {
        var decision = new ChatRouteDecision
        {
            Primary = MemberChatWorkflow.Analysis,
            RunnerUp = MemberChatWorkflow.Analysis,
        };
        Assert.False(decision.NeedsClarify);
    }

    // ---- the rendered prompt --------------------------------------------------------------

    [Fact]
    public void RoutingPrompt_CarriesEveryRenderedEntry_AndNoDataVocabulary()
    {
        var prompt = ChatRouterService.BuildPrompt("How did he sleep last night?", null);

        foreach (var entry in CardiTrack.Application.Services.ChatWorkflowCatalogue.Routable)
            Assert.Contains($"- {entry.Label}:", prompt);

        // The lean-classifier rule: no registry lines, no dataset names, no member context. The
        // registry's source names are the canary — if one appears, grounding has crept back in.
        Assert.DoesNotContain("RecentActivity", prompt);
        Assert.DoesNotContain("Baseline", prompt);
        Assert.DoesNotContain("UnresolvedAlerts", prompt);
        Assert.DoesNotContain("clarify", prompt);
    }

    [Fact]
    public void RoutingPrompt_IncludesHistoryOnlyWhenThereIsAny()
    {
        var bare = ChatRouterService.BuildPrompt("why?", null);
        var followed = ChatRouterService.BuildPrompt("why?", "--- Earlier turns ---\nCaregiver: how did he sleep?");

        Assert.DoesNotContain("follow-up", bare);
        Assert.Contains("how did he sleep?", followed);
    }
}
