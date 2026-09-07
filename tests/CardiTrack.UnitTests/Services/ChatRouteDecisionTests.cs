using CardiTrack.Application.DTOs.Common;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Services;
using CardiTrack.Domain.Enums;
using CardiTrack.Infrastructure.Services;
using NSubstitute;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// The routing decision's pure half: label parsing against the catalogue, the ladder's neighbour
/// relation, when clarify fires, and the closed reading/topic labels the zero-model rungs
/// consume. These are the rules §5 of the design states in prose; a router whose model calls
/// are mocked out is exactly these rules.
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
    // Two reading rungs, non-adjacent: one ask at two heights, so the tie-break serves it either
    // way. "How is he today" routes status with inference behind it and must be answered, not asked
    // back — clarify on that message would fire on the app's most common question.
    [InlineData(MemberChatWorkflow.Status, MemberChatWorkflow.Inference, false)]
    [InlineData(MemberChatWorkflow.Status, MemberChatWorkflow.Investigation, false)]
    [InlineData(MemberChatWorkflow.Analysis, MemberChatWorkflow.Investigation, false)]
    [InlineData(MemberChatWorkflow.Status, MemberChatWorkflow.Advise, true)]     // §5's own example
    [InlineData(MemberChatWorkflow.SteerCasual, MemberChatWorkflow.Analysis, true)]  // §5's other example
    [InlineData(MemberChatWorkflow.SteerOffTopic, MemberChatWorkflow.Analysis, true)]
    [InlineData(MemberChatWorkflow.Analysis, MemberChatWorkflow.Advise, true)]
    [InlineData(MemberChatWorkflow.Inference, MemberChatWorkflow.Advise, true)]
    [InlineData(MemberChatWorkflow.SteerCasual, MemberChatWorkflow.Advise, true)]
    public void Clarify_FiresOnlyWhenTheTwoCandidatesAreDifferentAsks(
        MemberChatWorkflow primary, MemberChatWorkflow runnerUp, bool expectClarify)
    {
        var decision = new ChatRouteDecision { Primary = primary, RunnerUp = runnerUp };

        Assert.Equal(expectClarify, decision.NeedsClarify);
        // Symmetric: which of the two the model happened to put first must not change the answer.
        Assert.Equal(expectClarify,
            new ChatRouteDecision { Primary = runnerUp, RunnerUp = primary }.NeedsClarify);
    }

    /// <summary>
    /// The shape the dispatch resolves without asking when a suggestion exists: advise against
    /// either steer, in either order. Still a clarify by <see cref="ChatRouteDecision.NeedsClarify"/>
    /// — the record cannot know whether a row exists — which is why the pair is named separately.
    /// </summary>
    [Theory]
    [InlineData(MemberChatWorkflow.SteerOffTopic, MemberChatWorkflow.Advise, true)]
    [InlineData(MemberChatWorkflow.Advise, MemberChatWorkflow.SteerOffTopic, true)]
    [InlineData(MemberChatWorkflow.SteerCasual, MemberChatWorkflow.Advise, true)]
    [InlineData(MemberChatWorkflow.Status, MemberChatWorkflow.Advise, false)]        // a reading against advise is a real ask
    [InlineData(MemberChatWorkflow.SteerOffTopic, MemberChatWorkflow.Analysis, false)] // a steer against a reading, too
    [InlineData(MemberChatWorkflow.Advise, MemberChatWorkflow.Advise, false)]
    public void AdviseAgainstASteer_IsNamedAsSuch(
        MemberChatWorkflow primary, MemberChatWorkflow runnerUp, bool expected)
    {
        var decision = new ChatRouteDecision { Primary = primary, RunnerUp = runnerUp };

        Assert.Equal(expected, decision.PitsAdviseAgainstASteer);
        if (expected)
            Assert.True(decision.NeedsClarify, "the pair is still a different ask until the dispatch finds a row.");
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

        foreach (var label in ChatRouteDecision.MetricLabels)
            Assert.Contains(label, prompt);
        foreach (var label in ChatRouteDecision.AdviseTopicLabels)
            Assert.Contains(label, prompt);
        Assert.Contains("namedMetric", prompt);
        Assert.Contains("asksForSpecifics", prompt);
    }

    [Theory]
    [InlineData("steps", StatusMetric.Steps)]
    [InlineData("restingHeartRate", StatusMetric.RestingHeartRate)]
    [InlineData("HRV", StatusMetric.HeartRateVariability)]
    [InlineData("oxygen", StatusMetric.Oxygen)]
    [InlineData("breathing", StatusMetric.BreathingRate)]
    [InlineData("sleep", StatusMetric.Sleep)]
    [InlineData("  Steps  ", StatusMetric.Steps)]
    public void ParseMetric_MapsClosedLabels(string label, StatusMetric expected) =>
        Assert.Equal(expected, ChatRouteDecision.ParseMetric(label));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("all")]
    [InlineData("moved")]
    [InlineData("heart")]
    [InlineData("2")]
    public void ParseMetric_DropsUnknownAndAll(string? label) =>
        Assert.Null(ChatRouteDecision.ParseMetric(label));

    [Theory]
    [InlineData("all", true)]
    [InlineData("ALL", true)]
    [InlineData("steps", false)]
    [InlineData("moved", false)]
    [InlineData(null, false)]
    public void ParseAllReadings_IsOnlyTheAllLabel(string? label, bool expected) =>
        Assert.Equal(expected, ChatRouteDecision.ParseAllReadings(label));

    [Theory]
    [InlineData("activity", AdviseTopic.Activity)]
    [InlineData("sleep", AdviseTopic.Sleep)]
    [InlineData("heart", AdviseTopic.HeartRate)]
    [InlineData("HEART", AdviseTopic.HeartRate)]
    public void ParseAdviseTopic_MapsClosedLabels(string label, AdviseTopic expected) =>
        Assert.Equal(expected, ChatRouteDecision.ParseAdviseTopic(label));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("general")]
    [InlineData("steps")]
    [InlineData("exercise")]
    public void ParseAdviseTopic_DropsUnknownAndGeneral(string? label) =>
        Assert.Null(ChatRouteDecision.ParseAdviseTopic(label));

    [Fact]
    public void RoutingPrompt_IncludesHistoryOnlyWhenThereIsAny()
    {
        var bare = ChatRouterService.BuildPrompt("why?", null);
        var followed = ChatRouterService.BuildPrompt("why?", "--- Earlier turns ---\nCaregiver: how did he sleep?");

        Assert.DoesNotContain("follow-up", bare);
        Assert.Contains("how did he sleep?", followed);
    }

    [Fact]
    public async Task RouteAsync_MapsClosedMetricAndTopicFields()
    {
        var result = await RouteMapped("steps", "activity", asksForSpecifics: true);

        Assert.Equal(MemberChatWorkflow.Status, result.Primary);
        Assert.Equal(StatusMetric.Steps, result.NamedMetric);
        Assert.False(result.AllReadings);
        Assert.Equal(AdviseTopic.Activity, result.AdviseTopic);
        Assert.True(result.AsksForSpecifics);
    }

    [Fact]
    public async Task RouteAsync_AllLabel_IsReadingsNotAMetric()
    {
        var result = await RouteMapped("all", adviseTopic: null, asksForSpecifics: false);

        Assert.Null(result.NamedMetric);
        Assert.True(result.AllReadings);
        Assert.Null(result.AdviseTopic);
        Assert.False(result.AsksForSpecifics);
    }

    [Fact]
    public async Task RouteAsync_UnknownMetricAndGeneralTopic_Drop()
    {
        var result = await RouteMapped("moved", "general", asksForSpecifics: false);

        Assert.Null(result.NamedMetric);
        Assert.False(result.AllReadings);
        Assert.Null(result.AdviseTopic);
    }

    private static async Task<ChatRouteDecision> RouteMapped(
        string? namedMetric, string? adviseTopic, bool asksForSpecifics)
    {
        var rewrite = Substitute.For<IRewriteAiService>();
        rewrite.GenerateStructuredWithUsageAsync<ChatRouterService.ChatRouteAiResponse>(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new AiGenerationResult<ChatRouterService.ChatRouteAiResponse>(
                new ChatRouterService.ChatRouteAiResponse
                {
                    Workflow = "status",
                    NamedMetric = namedMetric,
                    AdviseTopic = adviseTopic,
                    AsksForSpecifics = asksForSpecifics,
                },
                new AiUsage { ModelName = "test-router" }));

        var generated = await new ChatRouterService(rewrite).RouteAsync("has he moved much?");
        return generated.Result;
    }
}
