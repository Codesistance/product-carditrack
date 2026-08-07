using System.Net.Http.Headers;
using System.Text;
using CardiTrack.Shared.Json;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CardiTrack.Infrastructure.ExternalClients;

/// <summary>
/// Fitbit-provider client backed by the Google Health API v4 (the legacy Fitbit Web API is
/// decommissioned September 2026). Daily metrics use per-data-type dataPoints:dailyRollUp;
/// sleep uses the dataPoints list method filtered to the civil date.
/// Field names marked (assumed) below follow the documented `{field}_{aggregation}` convention
/// but are not yet verified against a live sandbox — pending Google console access.
/// </summary>
public class FitbitApiClient : IFitbitApiClient, IDeviceApiClient
{
    private readonly HttpClient _httpClient;

    public FitbitApiClient(IHttpClientFactory httpClientFactory)
    {
        _httpClient = httpClientFactory.CreateClient("FitbitClient");
    }

    public async Task<FitbitActivitiesResult> GetActivitiesAsync(string accessToken, DateOnly date)
    {
        // The five rollups are independent — issue them concurrently.
        var stepsTask = DailyRollupValueAsync(accessToken, "steps", date);
        var distanceTask = DailyRollupValueAsync(accessToken, "distance", date);
        var activeMinutesTask = DailyRollupValueAsync(accessToken, "active-minutes", date);
        var caloriesTask = DailyRollupValueAsync(accessToken, "total-calories", date);
        var floorsTask = DailyRollupValueAsync(accessToken, "floors", date);
        await Task.WhenAll(stepsTask, distanceTask, activeMinutesTask, caloriesTask, floorsTask);

        var steps = ReadInt(stepsTask.Result, "count") ?? 0;
        var distanceMeters = ReadDecimal(distanceTask.Result, "meters_sum", "meters", "count") ?? 0; // (assumed)
        var activeMinutes = ReadInt(activeMinutesTask.Result, "minutes_sum", "minutes", "count") ?? 0; // (assumed)
        var calories = ReadInt(caloriesTask.Result, "kilocalories_sum", "kilocalories", "count") ?? 0; // (assumed)
        var floors = ReadInt(floorsTask.Result, "count", "floors_sum") ?? 0; // (assumed)

        // The Health API has no sedentary-minutes data type. Report it as null, not 0: the
        // multi-device merge coalesces on the first non-null value, and a placeholder 0 from a
        // higher-priority device would beat another device's genuine reading.
        return new FitbitActivitiesResult(
            steps, decimal.Round(distanceMeters / 1000m, 3), activeMinutes, null, floors, calories);
    }

    public async Task<FitbitHeartRateResult> GetHeartRateAsync(string accessToken, DateOnly date)
    {
        var heartRate = await DailyRollupValueAsync(accessToken, "heart-rate", date);

        var minHr = ReadInt(heartRate, "beatsPerMinute_min");
        var maxHr = ReadInt(heartRate, "beatsPerMinute_max");
        var avgHr = ReadInt(heartRate, "beatsPerMinute_avg");

        // Daily resting HR sits on its own data type; tolerate its absence rather than failing
        // the whole snapshot — the rollup union name is (assumed).
        int? restingHr = null;
        try
        {
            var resting = await DailyRollupValueAsync(accessToken, "resting-heart-rate", date);
            restingHr = ReadInt(resting, "beatsPerMinute_avg", "beatsPerMinute", "count");
        }
        catch (FitbitApiException ex) when (ex.StatusCode is 400 or 404)
        {
        }

        return new FitbitHeartRateResult(restingHr, avgHr, maxHr, minHr);
    }

    public async Task<FitbitSleepResult> GetSleepAsync(string accessToken, DateOnly date)
    {
        // Sleep is session-shaped, so it uses list (get/list are its documented methods) with a
        // civil end-time filter: sessions that ended on the requested date.
        var filter = Uri.EscapeDataString(
            $"sleep.interval.end_time >= \"{date:yyyy-MM-dd}\" AND sleep.interval.end_time < \"{date.AddDays(1):yyyy-MM-dd}\"");
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/v4/users/me/dataTypes/sleep/dataPoints?filter={filter}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await _httpClient.SendAsync(request);
        await EnsureSuccessAsync(response);

        var root = await ParseBodyAsync(response, "sleep");

        // Session field names are (assumed) pending sandbox verification — parse null-safe.
        var sleep = (root["dataPoints"] as JArray)?.OfType<JObject>().FirstOrDefault()?["sleep"];

        DateTime? startTime = null, endTime = null;
        if (DateTime.TryParse(sleep?["interval"]?.Value<string>("startTime"), out var stParsed))
            startTime = stParsed;
        if (DateTime.TryParse(sleep?["interval"]?.Value<string>("endTime"), out var etParsed))
            endTime = etParsed;

        var efficiency = ReadInt(sleep, "efficiency");

        var stageSummary = sleep?["stageSummary"];
        var deep = ReadInt(stageSummary, "deepSleepMinutes", "deepMinutes", "deep");
        var light = ReadInt(stageSummary, "lightSleepMinutes", "lightMinutes", "light");
        var rem = ReadInt(stageSummary, "remSleepMinutes", "remMinutes", "rem");
        var awake = ReadInt(stageSummary, "awakeMinutes", "wakeMinutes", "awake");

        var totalMinutes = ReadInt(sleep, "totalSleepMinutes", "asleepMinutes")
            ?? (deep.HasValue || light.HasValue || rem.HasValue
                ? (deep ?? 0) + (light ?? 0) + (rem ?? 0)
                : (int?)null)
            ?? (startTime.HasValue && endTime.HasValue
                ? (int)(endTime.Value - startTime.Value).TotalMinutes - (awake ?? 0)
                : 0);

        return new FitbitSleepResult(totalMinutes, efficiency, startTime, endTime, deep, light, rem, awake);
    }

    public async Task<DeviceHealthSnapshot> GetHealthSnapshotAsync(string accessToken, DateOnly date)
    {
        var activitiesTask = GetActivitiesAsync(accessToken, date);
        var heartRateTask = GetHeartRateAsync(accessToken, date);
        var sleepTask = GetSleepAsync(accessToken, date);
        await Task.WhenAll(activitiesTask, heartRateTask, sleepTask);

        var activities = activitiesTask.Result;
        var heartRate = heartRateTask.Result;
        var sleep = sleepTask.Result;

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

    /// <summary>
    /// POSTs a one-day dailyRollUp for a data type and returns the rollup point's union value
    /// object (e.g. the "heartRate" member for data type "heart-rate"), or null when the day has
    /// no data. The union member is the camelCase form of the kebab-case data type name.
    /// </summary>
    private async Task<JToken?> DailyRollupValueAsync(string accessToken, string dataType, DateOnly date)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/v4/users/me/dataTypes/{dataType}/dataPoints:dailyRollUp");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var body = new JObject
        {
            // Closed-open civil interval covering the single requested day.
            ["range"] = new JObject
            {
                ["start"] = CivilDate(date),
                ["end"] = CivilDate(date.AddDays(1)),
            },
            ["windowSizeDays"] = 1,
        };
        request.Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request);
        await EnsureSuccessAsync(response);

        var root = await ParseBodyAsync(response, dataType);
        var point = (root["rollupDataPoints"] as JArray)?.OfType<JObject>().FirstOrDefault();
        return point?[ToCamelCase(dataType)];
    }

    private static JObject CivilDate(DateOnly date) => new()
    {
        ["year"] = date.Year,
        ["month"] = date.Month,
        ["day"] = date.Day,
    };

    /// <summary>kebab-case data type name → camelCase union member ("heart-rate" → "heartRate").</summary>
    private static string ToCamelCase(string dataType)
    {
        var parts = dataType.Split('-');
        return parts[0] + string.Concat(parts.Skip(1).Select(p =>
            char.ToUpperInvariant(p[0]) + p[1..]));
    }

    private static int? ReadInt(JToken? obj, params string[] names)
    {
        var value = ReadDecimal(obj, names);
        return value.HasValue ? (int)decimal.Round(value.Value) : null;
    }

    private static decimal? ReadDecimal(JToken? obj, params string[] names)
    {
        if (obj is not JObject o)
            return null;
        foreach (var name in names)
        {
            if (o[name] is JValue { Type: JTokenType.Integer or JTokenType.Float } v)
                return v.Value<decimal>();
        }
        return null;
    }

    private static async Task<JToken> ParseBodyAsync(HttpResponseMessage response, string what)
    {
        var body = await response.Content.ReadAsStringAsync();
        if (!JsonUtility.TryParse(body, out var root, out var errors))
            throw new FitbitApiException((int)response.StatusCode,
                $"Google Health API {what} response was not valid JSON: {string.Join("; ", errors)}. Payload: {JsonUtility.PreviewOf(body)}");
        return root!;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new FitbitApiException((int)response.StatusCode,
                $"Google Health API returned {(int)response.StatusCode}: {body}");
        }
    }
}
