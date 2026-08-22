using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Application.Services;
using CardiTrack.Mobile.Core.Alerts;

namespace CardiTrack.UnitTests.Mobile;

/// <summary>
/// Alert rules read availability-first, then alphabetically: the switches a caregiver can actually
/// use come before the reserved "Soon" ids, and within each group the title is what decides, so a
/// caregiver looking for one named rule can guess where it sits.
/// </summary>
public class AlertRuleOrderTests
{
    [Fact]
    public void Available_rules_come_before_coming_soon_ones()
    {
        var ordered = AlertRuleOrder.ForDisplay(
        [
            Rule("Zebra", implemented: false),
            Rule("Alpha", implemented: false),
            Rule("Yankee", implemented: true),
            Rule("Bravo", implemented: true),
        ]);

        Assert.Equal(["Bravo", "Yankee", "Alpha", "Zebra"], ordered.Select(r => r.Title));
    }

    [Fact]
    public void Each_group_is_alphabetical_by_title()
    {
        var ordered = AlertRuleOrder.ForDisplay(
        [
            Rule("Charlie", implemented: true),
            Rule("alpha", implemented: true),
            Rule("Bravo", implemented: true),
        ]);

        // Case-insensitive: "alpha" leads despite its lowercase a, which an ordinal sort would
        // have pushed below every capitalised title.
        Assert.Equal(["alpha", "Bravo", "Charlie"], ordered.Select(r => r.Title));
    }

    [Fact]
    public void The_catalogue_declaration_order_no_longer_decides()
    {
        // Sleep as the catalogue declares it. Before this, a stable sort left the "Soon" rules in
        // declaration order, which is not something a reader could predict. The available rules
        // lead, alphabetically among themselves — "Long daytime rest" joined them when the
        // activity-level intervals gave daytime_inactivity_block something to read.
        var sleep = AlertRuleCatalogue.Clusters.Single(c => c.Id == AlertRuleCatalogue.ClusterSleep);
        var ordered = AlertRuleOrder.ForDisplay(sleep.Rules.Select(Rule));

        Assert.Equal(
            ["Long daytime rest", "Unusual sleep length", "Late or missed bedtime", "Restless night"],
            ordered.Select(r => r.Title));
    }

    [Fact]
    public void A_cluster_of_only_coming_soon_rules_is_still_alphabetical()
    {
        var ordered = AlertRuleOrder.ForDisplay(
        [
            Rule("Restless night", implemented: false),
            Rule("Late or missed bedtime", implemented: false),
        ]);

        Assert.Equal(["Late or missed bedtime", "Restless night"], ordered.Select(r => r.Title));
    }

    [Fact]
    public void Ordering_keeps_every_rule_and_invents_none()
    {
        var rules = AlertRuleCatalogue.Clusters.SelectMany(c => c.Rules).Select(Rule).ToList();

        var ordered = AlertRuleOrder.ForDisplay(rules);

        Assert.Equal(rules.Count, ordered.Count);
        Assert.Equal(rules.Select(r => r.Id).OrderBy(id => id), ordered.Select(r => r.Id).OrderBy(id => id));
    }

    [Fact]
    public void A_missing_rule_list_is_an_empty_one()
    {
        // The renderer walks this straight into a foreach; a null cluster must not take the page
        // down the way a throw inside Apply already has once.
        Assert.Empty(AlertRuleOrder.ForDisplay(null));
    }

    private static AlertRuleSettingResponse Rule(string title, bool implemented) =>
        new() { Id = title.ToLowerInvariant(), Title = title, Description = title, IsImplemented = implemented };

    private static AlertRuleSettingResponse Rule(AlertRuleDefinition definition) =>
        new()
        {
            Id = definition.Id,
            Title = definition.Title,
            Description = definition.Description,
            IsImplemented = definition.IsImplemented,
        };
}
