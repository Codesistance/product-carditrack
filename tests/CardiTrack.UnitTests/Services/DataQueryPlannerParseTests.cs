using CardiTrack.Application.DTOs.Common;
using CardiTrack.Infrastructure.Services;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// The planner reads a small model's answer about which data a caregiver's question needs. Every
/// case here is malformed output, because that is the only kind this parse exists to survive — a
/// well-formed plan needs no defending. What it must never do is let unusable output through
/// wearing the shape of a real answer, since a plan that names nothing real fetches nothing and
/// looks exactly like a plan that deliberately asked for nothing.
/// </summary>
public class DataQueryPlannerParseTests
{
    private static DataQueryPlan Parse(IReadOnlyList<string> sources, IReadOnlyList<string>? metrics = null) =>
        DataQueryPlannerService.Parse(new DataQueryPlannerService.DataQueryPlanAiResponse
        {
            Sources = sources,
            Metrics = metrics ?? [],
        });

    /// <summary>
    /// <c>Enum.TryParse</c> succeeds on any numeric string, defined member or not — so without an
    /// <c>IsDefined</c> check "999" became a source the whitelist could never match, and the
    /// caregiver got a confident answer built on no data at all.
    /// </summary>
    [Theory]
    [InlineData("999")]
    [InlineData("-1")]
    [InlineData("0.5")]
    [InlineData("RecentActivityLogs")]
    [InlineData("")]
    public void Parse_DropsASourceThatNamesNoDefinedMember(string source) =>
        Assert.Empty(Parse([source]).Sources);

    [Fact]
    public void Parse_KeepsTheRealSourcesBesideTheJunk()
    {
        var plan = Parse(["RecentActivity", "999", "unresolvedalerts"]);

        Assert.Equal([DataQueryKind.RecentActivity, DataQueryKind.UnresolvedAlerts], plan.Sources);
    }

    [Theory]
    [InlineData("999")]
    [InlineData("-1")]
    public void Parse_TreatsAnUnreadableMetricListAsNoAnswer(string metric)
    {
        // Null, not empty: empty means the model said "general question" and every series is
        // wanted. A list of names none of which parsed says nothing about the question, and
        // collapsing the two would silently turn junk into a deliberate answer.
        Assert.Null(Parse(["RecentActivity"], [metric]).ChartMetrics);
    }

    /// <summary>
    /// Empty means the model answered "this question is general" and every chart is wanted; null
    /// means it answered with names none of which parsed, and we know nothing. Both widen, but
    /// only one of them is the model agreeing to something.
    /// </summary>
    [Fact]
    public void Parse_KeepsADeliberatelyEmptyListApartFromAnUnreadableOne()
    {
        Assert.Empty(Parse(["RecentActivity"], []).ChartMetrics!);
        Assert.Null(Parse(["RecentActivity"], ["999"]).ChartMetrics);
    }

    /// <summary>
    /// The regression this whole change exists for: "show me his steps this week" came back with
    /// metrics omitted, so the caregiver got steps, heart rate and sleep. The field is required in
    /// the schema now, which makes omission impossible — this pins the half that parsing owns,
    /// that a single named metric narrows to exactly that metric.
    /// </summary>
    [Fact]
    public void Parse_NarrowsToTheOneMetricAQuestionNamed() =>
        Assert.Equal([ChartMetricKind.Steps], Parse(["RecentActivity"], ["Steps"]).ChartMetrics);

    [Fact]
    public void Parse_KeepsTheRecognisedMetricsWhenOnlySomeParse()
    {
        var plan = Parse(["RecentActivity"], ["steps", "999", "Sleep"]);

        Assert.Equal([ChartMetricKind.Steps, ChartMetricKind.Sleep], plan.ChartMetrics);
    }

    /// <summary>
    /// Zero recognised sources is a valid plan, not a failure — it means the clinical step answers
    /// from the member's demographic and notes context alone, which is right for a question that
    /// needs none of the four data sources.
    /// </summary>
    [Fact]
    public void Parse_DefaultsTheWindows_WhenTheModelOmitsThem()
    {
        var plan = Parse([]);

        Assert.Empty(plan.Sources);
        Assert.Equal(7, plan.RecentActivityDays);
        Assert.Equal(24, plan.RealtimeAssessmentHours);
    }
}
