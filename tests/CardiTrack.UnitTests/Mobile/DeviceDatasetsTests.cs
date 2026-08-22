using CardiTrack.Mobile.Core.Devices;

namespace CardiTrack.UnitTests.Mobile;

public class DeviceDatasetsTests
{
    private const string ActivityScope =
        "https://www.googleapis.com/auth/googlehealth.activity_and_fitness.readonly";
    private const string MetricsScope =
        "https://www.googleapis.com/auth/googlehealth.health_metrics_and_measurements.readonly";
    private const string SleepScope =
        "https://www.googleapis.com/auth/googlehealth.sleep.readonly";
    private const string EcgScope =
        "https://www.googleapis.com/auth/googlehealth.ecg.readonly";
    private const string IrnScope =
        "https://www.googleapis.com/auth/googlehealth.irn.readonly";

    [Fact]
    public void For_GoogleHealthScopes_ReturnsDatasetsNotScopeUris()
    {
        var datasets = DeviceDatasets.For([ActivityScope, MetricsScope, SleepScope]);

        Assert.Equal(
            ["Steps", "Distance", "Active Minutes", "Floors", "Calories",
             "Heart Rate", "Resting HR", "HRV",
             "Sleep", "Sleep Stages",
             "Weight", "SpO2", "VO2 Max", "Breathing Rate", "Temperature", "Blood Sugar"],
            datasets.Select(d => d.Name));
    }

    [Fact]
    public void For_GoogleHealthScopes_GroupsPillsByFamily()
    {
        var datasets = DeviceDatasets.For([SleepScope, MetricsScope, ActivityScope]);

        Assert.Equal(
            [DatasetFamily.Activity, DatasetFamily.Activity, DatasetFamily.Activity,
             DatasetFamily.Activity, DatasetFamily.Activity,
             DatasetFamily.Heart, DatasetFamily.Heart, DatasetFamily.Heart,
             DatasetFamily.Sleep, DatasetFamily.Sleep,
             DatasetFamily.Body, DatasetFamily.Body, DatasetFamily.Body, DatasetFamily.Body,
             DatasetFamily.Body, DatasetFamily.Body],
            datasets.Select(d => d.Family));
    }

    // Issue #82 added four body readings under health_metrics_and_measurements; the 2026-08 sweep
    // added HRV, weight and blood sugar to the same bundle. All of them earn pills by the rule
    // every other pill follows — name what we actually pull. The three newest reuse the existing
    // Heart and Body families, so the row gains names but no new visual vocabulary.
    [Fact]
    public void For_MetricsScope_NamesTheBodyReadingsTheClientNowIngests()
    {
        var datasets = DeviceDatasets.For([MetricsScope]);

        Assert.Equal(
            ["Heart Rate", "Resting HR", "HRV", "Weight", "SpO2", "VO2 Max", "Breathing Rate",
             "Temperature", "Blood Sugar"],
            datasets.Select(d => d.Name));
        Assert.All(
            datasets.Where(d => d.Name is "SpO2" or "VO2 Max" or "Breathing Rate" or "Temperature"
                or "Weight" or "Blood Sugar"),
            d => Assert.Equal(DatasetFamily.Body, d.Family));
        Assert.Equal(DatasetFamily.Heart, datasets.Single(d => d.Name == "HRV").Family);
    }

    // The two rhythm bundles are read by the client, so they are named rather than humanised — and
    // both land in Heart, where a card that already shows heart rate gains no extra pill for them.
    [Fact]
    public void For_RhythmScopes_AreNamedDatasetsInTheHeartFamily()
    {
        var datasets = DeviceDatasets.For([EcgScope, IrnScope]);

        Assert.Equal(["ECG", "Irregular Rhythm"], datasets.Select(d => d.Name));
        Assert.All(datasets, d => Assert.Equal(DatasetFamily.Heart, d.Family));
    }

    [Fact]
    public void For_ScopeOrder_DoesNotChangeThePillOrder()
    {
        var granted = DeviceDatasets.For([ActivityScope, MetricsScope, SleepScope]);
        var reversed = DeviceDatasets.For([SleepScope, MetricsScope, ActivityScope]);

        Assert.Equal(granted, reversed);
    }

    [Theory]
    [InlineData("activity", new[] { "Steps", "Distance", "Active Minutes", "Floors", "Calories" })]
    [InlineData("heartrate", new[] { "Heart Rate", "Resting HR" })]
    [InlineData("sleep", new[] { "Sleep", "Sleep Stages" })]
    [InlineData("weight", new[] { "Weight" })]
    [InlineData("oxygen_saturation", new[] { "SpO2" })]
    [InlineData("spo2", new[] { "SpO2" })]
    [InlineData("profile", new[] { "Profile" })]
    public void For_LegacyFitbitScope_StillMaps(string scope, string[] expected)
    {
        Assert.Equal(expected, DeviceDatasets.For([scope]).Select(d => d.Name));
    }

    [Fact]
    public void For_ScopeWithoutTheUriPrefix_MapsTheSameAsTheFullUri()
    {
        Assert.Equal(
            DeviceDatasets.For([SleepScope]),
            DeviceDatasets.For(["googlehealth.sleep.readonly"]));
    }

    [Fact]
    public void For_MixedCaseAndPaddedScope_IsNormalised()
    {
        Assert.Equal(["Sleep", "Sleep Stages"], DeviceDatasets.For(["  SLEEP  "]).Select(d => d.Name));
    }

    [Fact]
    public void For_ScopesGrantingTheSameDataset_EmitsOnePill()
    {
        var datasets = DeviceDatasets.For([ActivityScope, "activity"]);

        Assert.Equal(["Steps", "Distance", "Active Minutes", "Floors", "Calories"],
            datasets.Select(d => d.Name));
    }

    [Fact]
    public void For_UnknownScope_IsHumanisedAndNeverRendersAUri()
    {
        // A scope we do not map — the provider's own spelling of a bundle CardiTrack does not read.
        var datasets = DeviceDatasets.For(
            ["https://www.googleapis.com/auth/googlehealth.reproductive_health.readonly"]);

        var dataset = Assert.Single(datasets);
        Assert.Equal("Reproductive Health", dataset.Name);
        Assert.Equal(DatasetFamily.Other, dataset.Family);
    }

    [Fact]
    public void For_UnknownScopeWithAnAcronym_KeepsTheAcronymUppercase()
    {
        // "hrv" is a mapped bundle name no longer, but the acronym table still has to carry any
        // unmapped scope that contains one — here the provider's ECG bundle under a spelling this
        // mapping does not know.
        var datasets = DeviceDatasets.For(["googlehealth.ecg_history.readonly", "HEALTH_API"]);

        Assert.Equal(["ECG History", "Health API"], datasets.Select(d => d.Name));
    }

    [Fact]
    public void For_UnknownScopes_SortAfterTheKnownOnes()
    {
        var datasets = DeviceDatasets.For(["something_new", SleepScope]);

        Assert.Equal(["Sleep", "Sleep Stages", "Something New"], datasets.Select(d => d.Name));
    }

    [Fact]
    public void For_RepeatedUnknownScope_EmitsOnePill()
    {
        var datasets = DeviceDatasets.For(["something_new", "SOMETHING-NEW"]);

        Assert.Equal(["Something New"], datasets.Select(d => d.Name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void For_BlankScope_IsIgnored(string scope)
    {
        Assert.Empty(DeviceDatasets.For([scope]));
    }

    [Fact]
    public void For_NoScopes_ReturnsEmpty()
    {
        Assert.Empty(DeviceDatasets.For([]));
        Assert.Empty(DeviceDatasets.For(null));
    }

    [Fact]
    public void GroupedFor_FullGrant_CollapsesEveryDatasetToFourPills()
    {
        var groups = DeviceDatasets.GroupedFor([ActivityScope, MetricsScope, SleepScope]);

        Assert.Equal(
            [DatasetFamily.Activity, DatasetFamily.Heart, DatasetFamily.Sleep, DatasetFamily.Body],
            groups.Select(g => g.Family));
        Assert.Equal(["Activity", "Heart", "Sleep", "Body"], groups.Select(g => g.Label));
        Assert.Equal([5, 3, 2, 6], groups.Select(g => g.Count));
    }

    // The bound the family row exists for: granting both rhythm scopes on top of a full grant adds
    // two named datasets and not one pill, because Heart is already on the card.
    [Fact]
    public void GroupedFor_RhythmScopes_AddNoPillToACardThatAlreadyShowsHeart()
    {
        var without = DeviceDatasets.GroupedFor([ActivityScope, MetricsScope, SleepScope]);
        var with = DeviceDatasets.GroupedFor([ActivityScope, MetricsScope, SleepScope, EcgScope, IrnScope]);

        Assert.Equal(without.Count, with.Count);
        Assert.Equal(without.Sum(g => g.Datasets.Count) + 2, with.Sum(g => g.Datasets.Count));
    }

    // The body readings under health_metrics_and_measurements cost one pill between them, not one
    // each — the guard that the row stays bounded as the mapping grows.
    [Fact]
    public void GroupedFor_TheBodyReadings_CostOnePillBetweenThem()
    {
        var withoutBody = DeviceDatasets.GroupedFor([ActivityScope, "heartrate", SleepScope]);
        var withBody = DeviceDatasets.GroupedFor([ActivityScope, MetricsScope, SleepScope]);

        Assert.Equal(3, withoutBody.Count);
        Assert.Equal(4, withBody.Count);
        Assert.Equal(9, withoutBody.Sum(g => g.Datasets.Count));
        Assert.Equal(16, withBody.Sum(g => g.Datasets.Count));
    }

    [Fact]
    public void GroupedFor_FamilyWithOneDataset_LabelsThePillWithTheDatasetNotTheFamily()
    {
        var group = Assert.Single(DeviceDatasets.GroupedFor(["weight"]));

        Assert.Equal("Weight", group.Label);
        Assert.Null(group.Count);
    }

    [Fact]
    public void GroupedFor_ScopeOrder_DoesNotChangeTheFamilyOrder()
    {
        string[] every = [SleepScope, "weight", MetricsScope, ActivityScope, "profile"];

        Assert.Equal(
            ["Activity", "Heart", "Sleep", "Body", "Other"],
            DeviceDatasets.GroupedFor(every.Reverse())
                .Select(g => DeviceDatasetGroup.DisplayName(g.Family)));
    }

    [Fact]
    public void GroupedFor_UnknownScopes_ShareTheOtherPill()
    {
        var groups = DeviceDatasets.GroupedFor([SleepScope, "irregular_rhythm", "something_new"]);

        var other = Assert.Single(groups, g => g.Family == DatasetFamily.Other);
        Assert.Equal("Other", other.Label);
        Assert.Equal(2, other.Count);
        Assert.Equal("Irregular Rhythm · Something New", other.Detail);
    }

    [Fact]
    public void GroupedFor_Detail_ListsTheDatasetsBehindThePill()
    {
        var groups = DeviceDatasets.GroupedFor([MetricsScope]);

        Assert.Equal("Heart Rate · Resting HR · HRV",
            Assert.Single(groups, g => g.Family == DatasetFamily.Heart).Detail);
        Assert.Equal("Weight · SpO2 · VO2 Max · Breathing Rate · Temperature · Blood Sugar",
            Assert.Single(groups, g => g.Family == DatasetFamily.Body).Detail);
    }

    [Fact]
    public void GroupedFor_NoScopes_ReturnsEmpty()
    {
        Assert.Empty(DeviceDatasets.GroupedFor([]));
        Assert.Empty(DeviceDatasets.GroupedFor(null));
    }
}
