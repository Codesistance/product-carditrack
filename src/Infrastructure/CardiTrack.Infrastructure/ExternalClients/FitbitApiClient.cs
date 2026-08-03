using System.Net.Http.Headers;
using CardiTrack.Shared.Json;
using Newtonsoft.Json.Linq;

namespace CardiTrack.Infrastructure.ExternalClients;

public class FitbitApiClient : IFitbitApiClient, IDeviceApiClient
{
    private readonly HttpClient _httpClient;

    public FitbitApiClient(IHttpClientFactory httpClientFactory)
    {
        _httpClient = httpClientFactory.CreateClient("FitbitClient");
    }

    public async Task<FitbitActivitiesResult> GetActivitiesAsync(string accessToken, DateOnly date)
    {
        var dateStr = date.ToString("yyyy-MM-dd");
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/1/user/-/activities/date/{dateStr}.json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await _httpClient.SendAsync(request);
        await EnsureSuccessAsync(response);

        var root = await ParseBodyAsync(response, "activities");
        var summary = root["summary"];

        var steps = summary?.Value<int?>("steps") ?? 0;
        var calories = summary?.Value<int?>("caloriesOut") ?? 0;
        var activeMinutes = (summary?.Value<int?>("fairlyActiveMinutes") ?? 0)
                          + (summary?.Value<int?>("veryActiveMinutes") ?? 0);
        var sedentaryMinutes = summary?.Value<int?>("sedentaryMinutes") ?? 0;
        var floors = summary?.Value<int?>("floors") ?? 0;

        decimal distanceKm = 0;
        if (summary?["distances"] is JArray distances)
        {
            foreach (var d in distances.OfType<JObject>())
            {
                if (d.Value<string>("activity") == "total" && d["distance"] is JValue dist)
                {
                    distanceKm = dist.Value<decimal>();
                    break;
                }
            }
        }

        return new FitbitActivitiesResult(steps, distanceKm, activeMinutes, sedentaryMinutes, floors, calories);
    }

    public async Task<FitbitHeartRateResult> GetHeartRateAsync(string accessToken, DateOnly date)
    {
        var dateStr = date.ToString("yyyy-MM-dd");
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/1/user/-/heart/date/{dateStr}/1d.json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await _httpClient.SendAsync(request);
        await EnsureSuccessAsync(response);

        var root = await ParseBodyAsync(response, "heart rate");

        int? restingHr = null;
        int? avgHr = null;
        int? maxHr = null;
        int? minHr = null;

        if (root["activities-heart"] is JArray heartRateDay && heartRateDay.Count > 0)
        {
            var value = heartRateDay[0]["value"];
            if (value is not null)
            {
                restingHr = value.Value<int?>("restingHeartRate");

                if (value["heartRateZones"] is JArray zones)
                {
                    foreach (var zone in zones.OfType<JObject>())
                    {
                        if (zone.Value<int?>("max") is { } mx) maxHr = mx;
                        if (zone.Value<int?>("min") is { } mn && minHr is null) minHr = mn;
                    }
                }
            }
        }

        return new FitbitHeartRateResult(restingHr, avgHr, maxHr, minHr);
    }

    public async Task<FitbitSleepResult> GetSleepAsync(string accessToken, DateOnly date)
    {
        var dateStr = date.ToString("yyyy-MM-dd");
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/1/user/-/sleep/date/{dateStr}.json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await _httpClient.SendAsync(request);
        await EnsureSuccessAsync(response);

        var root = await ParseBodyAsync(response, "sleep");
        var summary = root["summary"];

        var totalMinutes = summary?.Value<int?>("totalMinutesAsleep") ?? 0;
        int? efficiency = null;
        DateTime? startTime = null;
        DateTime? endTime = null;
        int? deep = null, light = null, rem = null, awake = null;

        if (root["sleep"] is JArray sleepArr && sleepArr.Count > 0)
        {
            var mainSleep = sleepArr[0];
            efficiency = mainSleep.Value<int?>("efficiency");
            if (DateTime.TryParse(mainSleep.Value<string>("startTime"), out var stParsed))
                startTime = stParsed;
            if (DateTime.TryParse(mainSleep.Value<string>("endTime"), out var etParsed))
                endTime = etParsed;

            var levelSummary = mainSleep["levels"]?["summary"];
            if (levelSummary is not null)
            {
                deep = levelSummary["deep"]?.Value<int?>("minutes");
                light = levelSummary["light"]?.Value<int?>("minutes");
                rem = levelSummary["rem"]?.Value<int?>("minutes");
                awake = levelSummary["wake"]?.Value<int?>("minutes");
            }
        }

        return new FitbitSleepResult(totalMinutes, efficiency, startTime, endTime, deep, light, rem, awake);
    }

    public async Task<DeviceHealthSnapshot> GetHealthSnapshotAsync(string accessToken, DateOnly date)
    {
        var activities = await GetActivitiesAsync(accessToken, date);
        var heartRate = await GetHeartRateAsync(accessToken, date);
        var sleep = await GetSleepAsync(accessToken, date);

        return new DeviceHealthSnapshot(
            activities.Steps,
            activities.DistanceKm,
            activities.ActiveMinutes,
            activities.SedentaryMinutes,
            activities.Floors,
            activities.CaloriesBurned,
            heartRate.RestingHeartRate,
            heartRate.AvgHeartRate,
            heartRate.MaxHeartRate,
            heartRate.MinHeartRate,
            sleep.TotalSleepMinutes,
            sleep.SleepEfficiency,
            sleep.SleepStartTime,
            sleep.SleepEndTime,
            sleep.DeepSleepMinutes,
            sleep.LightSleepMinutes,
            sleep.RemSleepMinutes,
            sleep.AwakeMinutes);
    }

    private static async Task<JToken> ParseBodyAsync(HttpResponseMessage response, string what)
    {
        var body = await response.Content.ReadAsStringAsync();
        if (!JsonUtility.TryParse(body, out var root, out var errors))
            throw new FitbitApiException((int)response.StatusCode,
                $"Fitbit {what} response was not valid JSON: {string.Join("; ", errors)}. Payload: {JsonUtility.PreviewOf(body)}");
        return root!;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new FitbitApiException((int)response.StatusCode,
                $"Fitbit API returned {(int)response.StatusCode}: {body}");
        }
    }
}
