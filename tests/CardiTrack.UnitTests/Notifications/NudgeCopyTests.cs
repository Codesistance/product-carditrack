using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Services.Notifications;
using CardiTrack.Domain.Enums;
using CardiTrack.Mobile.Core.Notifications;
using Xunit;

namespace CardiTrack.UnitTests.Notifications;

/// <summary>
/// The server stores keys and the client owns the words, so the one thing that can silently break
/// is the join between them: a rule shipping a key nothing renders.
/// </summary>
public class NudgeCopyTests
{
    [Fact]
    public void EveryRuleInTheCatalogueHasCopyForEveryKeyItCanEmit()
    {
        var contexts = new[]
        {
            new NudgeContextBuilder().NoConnections().Build(),
            new NudgeContextBuilder().AccountLevel().TimeZone("UTC").Build(),
            new NudgeContextBuilder().NoMedicalNotes().Build(),
            new NudgeContextBuilder().PausedUntil(NudgeContextBuilder.Now.AddDays(20)).Build(),
            new NudgeContextBuilder()
                .WithConnections(NudgeContextBuilder.Connection(ConnectionStatus.TokenExpired))
                .Build(),
            new NudgeContextBuilder()
                .WithConnections(NudgeContextBuilder.Connection(ConnectionStatus.AuthError))
                .Build(),
            new NudgeContextBuilder()
                .WithConnections(NudgeContextBuilder.Connection(
                    lastSync: NudgeContextBuilder.Now.AddDays(-9), scopes: "activity_and_fitness"))
                .Build(),
            new NudgeContextBuilder()
                .NoBaseline().DaysCaptured(4)
                .LastActivity(DateOnly.FromDateTime(NudgeContextBuilder.Now).AddDays(-20))
                .Build(),
            // All three battery variants. The band-only one is the reason they are three: its copy
            // must carry no {percent}, since the rule has no percentage to substitute.
            new NudgeContextBuilder()
                .WithConnections(NudgeContextBuilder.BatteryConnection(7))
                .Build(),
            new NudgeContextBuilder()
                .WithConnections(NudgeContextBuilder.BatteryConnection(null, status: "Low"))
                .Build(),
            new NudgeContextBuilder()
                .WithConnections(NudgeContextBuilder.BatteryConnection(null, status: "Empty"))
                .Build()
        };

        foreach (var rule in NudgeRuleCatalogue.All)
        {
            foreach (var verdict in contexts.Select(rule.Evaluate).Where(v => v.HasGap))
            {
                var response = new NotificationResponse
                {
                    RuleCode = rule.RuleCode,
                    TitleKey = NudgeRuleCatalogue.TitleKey(rule.RuleCode, verdict.Variant),
                    BodyKey = NudgeRuleCatalogue.BodyKey(rule.RuleCode, verdict.Variant),
                    BenefitKey = NudgeRuleCatalogue.BenefitKey(rule.RuleCode, verdict.Variant),
                    CardiMemberName = "Margaret",
                    TemplateData = System.Text.Json.JsonSerializer.Serialize(verdict.TemplateData)
                };

                Assert.NotEqual("CardiTrack needs a little more information.", NudgeCopy.Title(response));
                Assert.NotEqual("CardiTrack needs a little more information.", NudgeCopy.Body(response));
                Assert.NotEqual("CardiTrack needs a little more information.", NudgeCopy.Benefit(response));

                // An unfilled placeholder means the copy asked for a value the rule never emits.
                Assert.DoesNotContain("{", NudgeCopy.Title(response));
                Assert.DoesNotContain("{", NudgeCopy.Body(response));
                Assert.DoesNotContain("{", NudgeCopy.Benefit(response));
            }
        }
    }

    [Fact]
    public void AMissingNameFallsBackRatherThanRenderingAPlaceholder()
    {
        var response = new NotificationResponse
        {
            TitleKey = "nudge.DEVICE_REMOVED.title",
            CardiMemberName = null
        };

        Assert.Equal("your family member has no connected wearable", NudgeCopy.Title(response));
    }

    [Fact]
    public void UnreadableTemplateDataCostsTheNumbersRatherThanTheCard()
    {
        var response = new NotificationResponse
        {
            BodyKey = "nudge.BASELINE_STALLED.body",
            CardiMemberName = "Margaret",
            TemplateData = "not json"
        };

        // Still a sentence, still shown — just without its counters.
        Assert.StartsWith("{captured}", NudgeCopy.Body(response));
    }

    [Fact]
    public void AnUnknownKeyFallsBackInsteadOfThrowing()
    {
        var response = new NotificationResponse { TitleKey = "nudge.NOT_A_RULE.title" };
        Assert.Equal("CardiTrack needs a little more information.", NudgeCopy.Title(response));
    }
}
