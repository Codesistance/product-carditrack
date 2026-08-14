using CardiTrack.Application.DTOs.Responses;
using CardiTrack.Domain.Entities;

namespace CardiTrack.Application.Services;

/// <summary>
/// Builds <see cref="WeatherSnapshotResponse"/> from an <see cref="EnvironmentalReading"/> —
/// kept out of the DTO itself so that layer stays free of domain-entity references. Shared by
/// <see cref="DashboardService"/> and <see cref="CardiMemberService"/> so both read a reading
/// the same way.
/// </summary>
public static class WeatherSnapshotMapper
{
    /// <summary>
    /// Null when the member hasn't consented to environmental context, or nothing has been
    /// derived for them yet — either way there is nothing to show, so the card should hide.
    /// </summary>
    public static WeatherSnapshotResponse? From(bool consentGranted, EnvironmentalReading? reading)
    {
        if (!consentGranted || reading is null)
            return null;

        var condition = string.IsNullOrWhiteSpace(reading.WeatherCondition) ? null : reading.WeatherCondition;

        // A reading with every field empty is possible (the enrichment pass can succeed for one
        // field and fail for the rest) — nothing to show is nothing to show, same as no reading.
        if (reading.TemperatureCelsius is null && condition is null
            && reading.RelativeHumidityPercent is null && reading.AirQualityCategory is null
            && reading.AirQualityIndex is null)
        {
            return null;
        }

        return new WeatherSnapshotResponse
        {
            TemperatureCelsius = reading.TemperatureCelsius is { } c ? (decimal)c : null,
            Condition = condition,
            HumidityPercent = reading.RelativeHumidityPercent,
            AirQualityIndex = reading.AirQualityIndex,
            AirQualityCategory = reading.AirQualityCategory,
            AsOfUtc = reading.SessionEndUtc,
        };
    }
}
