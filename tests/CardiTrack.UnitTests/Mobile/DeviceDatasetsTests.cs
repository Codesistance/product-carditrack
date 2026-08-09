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

    [Fact]
    public void For_GoogleHealthScopes_ReturnsDatasetsNotScopeUris()
    {
        var datasets = DeviceDatasets.For([ActivityScope, MetricsScope, SleepScope]);

        Assert.Equal(
            ["Steps", "Distance", "Active Minutes", "Floors", "Calories",
             "Heart Rate", "Resting HR",
             "SpO2", "VO2 Max", "Breathing Rate", "Temperature",
             "Sleep", "Sleep Stages"],
            datasets.Select(d => d.Name));
    }

    [Fact]
    public void For_GoogleHealthScopes_GroupsPillsByFamily()
    {
        var datasets = DeviceDatasets.For([SleepScope, MetricsScope, ActivityScope]);

        Assert.Equal(
            [DatasetFamily.Activity, DatasetFamily.Activity, DatasetFamily.Activity,
             DatasetFamily.Activity, DatasetFamily.Activity,
             DatasetFamily.Heart, DatasetFamily.Heart,
             DatasetFamily.Body, DatasetFamily.Body, DatasetFamily.Body, DatasetFamily.Body,
             DatasetFamily.Sleep, DatasetFamily.Sleep],
            datasets.Select(d => d.Family));
    }

    // Issue #82: FitbitApiClient ingests these four under health_metrics_and_measurements, so by
    // the rule every other pill follows — name what we actually pull — they earn pills too. They
    // reuse the existing Body family, so the row gains pills but no new visual vocabulary.
    [Fact]
    public void For_MetricsScope_NamesTheBodyReadingsTheClientNowIngests()
    {
        var datasets = DeviceDatasets.For([MetricsScope]);

        Assert.Equal(
            ["Heart Rate", "Resting HR", "SpO2", "VO2 Max", "Breathing Rate", "Temperature"],
            datasets.Select(d => d.Name));
        Assert.All(
            datasets.Where(d => d.Name is "SpO2" or "VO2 Max" or "Breathing Rate" or "Temperature"),
            d => Assert.Equal(DatasetFamily.Body, d.Family));
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
        var datasets = DeviceDatasets.For(
            ["https://www.googleapis.com/auth/googlehealth.irregular_rhythm.readonly"]);

        var dataset = Assert.Single(datasets);
        Assert.Equal("Irregular Rhythm", dataset.Name);
        Assert.Equal(DatasetFamily.Other, dataset.Family);
    }

    [Fact]
    public void For_UnknownScopeWithAnAcronym_KeepsTheAcronymUppercase()
    {
        var datasets = DeviceDatasets.For(
            ["https://www.googleapis.com/auth/googlehealth.ecg.readonly", "HEALTH_API"]);

        Assert.Equal(["ECG", "Health API"], datasets.Select(d => d.Name));
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
}
