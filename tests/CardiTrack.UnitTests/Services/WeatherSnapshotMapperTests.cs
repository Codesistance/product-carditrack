using CardiTrack.Application.Services;
using CardiTrack.Domain.Entities;

namespace CardiTrack.UnitTests.Services;

public class WeatherSnapshotMapperTests
{
    private static readonly Guid MemberId = Guid.NewGuid();
    private static readonly DateTime SessionEnd = DateTime.UtcNow.AddHours(-1);

    private static EnvironmentalReading Reading(
        double? temperature = null, string? condition = null, int? humidity = null,
        int? airQualityIndex = null, string? airQualityCategory = null) => new()
    {
        CardiMemberId = MemberId,
        SessionEndUtc = SessionEnd,
        TemperatureCelsius = temperature,
        WeatherCondition = condition,
        RelativeHumidityPercent = humidity,
        AirQualityIndex = airQualityIndex,
        AirQualityCategory = airQualityCategory,
    };

    [Fact]
    public void From_IsNull_WhenConsentNotGranted() =>
        Assert.Null(WeatherSnapshotMapper.From(consentGranted: false, Reading(temperature: 21)));

    [Fact]
    public void From_IsNull_WhenNoReading() =>
        Assert.Null(WeatherSnapshotMapper.From(consentGranted: true, reading: null));

    [Fact]
    public void From_IsNull_WhenEveryFieldIsEmpty() =>
        Assert.Null(WeatherSnapshotMapper.From(consentGranted: true, Reading()));

    /// <summary>
    /// Regression: the emptiness check originally omitted AirQualityIndex, so a reading with only
    /// that field populated was wrongly treated as "nothing to show."
    /// </summary>
    [Fact]
    public void From_IsNotNull_WhenOnlyAirQualityIndexIsPresent()
    {
        var result = WeatherSnapshotMapper.From(consentGranted: true, Reading(airQualityIndex: 42));

        Assert.NotNull(result);
        Assert.Equal(42, result!.AirQualityIndex);
    }

    [Fact]
    public void From_TreatsWhitespaceOnlyCondition_AsAbsent()
    {
        var result = WeatherSnapshotMapper.From(
            consentGranted: true, Reading(temperature: 21, condition: "   "));

        Assert.NotNull(result);
        Assert.Null(result!.Condition);
    }

    [Fact]
    public void From_CarriesEveryField_WhenAllArePresent()
    {
        var reading = Reading(
            temperature: 21.4, condition: "Partly cloudy", humidity: 55,
            airQualityIndex: 42, airQualityCategory: "Good");

        var result = WeatherSnapshotMapper.From(consentGranted: true, reading);

        Assert.NotNull(result);
        Assert.Equal(21.4m, result!.TemperatureCelsius);
        Assert.Equal("Partly cloudy", result.Condition);
        Assert.Equal(55, result.HumidityPercent);
        Assert.Equal(42, result.AirQualityIndex);
        Assert.Equal("Good", result.AirQualityCategory);
        Assert.Equal(reading.SessionEndUtc, result.AsOfUtc);
    }
}
