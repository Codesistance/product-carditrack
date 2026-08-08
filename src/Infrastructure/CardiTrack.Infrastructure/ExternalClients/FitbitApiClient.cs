using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using CardiTrack.Shared.Json;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CardiTrack.Infrastructure.ExternalClients;

/// <summary>
/// Fitbit-provider client backed by the Google Health API v4 (the legacy Fitbit Web API is
/// decommissioned September 2026).
/// <para>
/// Reads follow the method each data type actually supports. Interval and Sample types
/// (steps, distance, active-minutes, total-calories, floors, heart-rate) take
/// `dataPoints:dailyRollUp`; Daily types (daily-resting-heart-rate) support only `list`\`reconcile`
/// and 400 on a rollup; Session types (sleep) take `list` with a civil-time filter.
/// </para>
/// <para>
/// Every field name below is the one in the v4 reference. Rollup values are named
/// `{field}{Aggregation}` in camelCase (`countSum`, `beatsPerMinuteAvg`) — an earlier snake_case
/// reading of that convention (`count`, `beatsPerMinute_avg`) matched nothing and silently
/// reported zeros. Units are the reference's own: distance in millimetres, calories in kcal.
/// </para>
/// </summary>
public class FitbitApiClient : IFitbitApiClient, IDeviceApiClient
{
    /// <summary>
    /// Activity levels that count as "active minutes", matching Fitbit's classic definition.
    /// `active-minutes` rolls up as a breakdown per level, so leaving LIGHTLY_ACTIVE in would
    /// report a number several times the one wearers see in the Fitbit app.
    /// </summary>
    private static readonly string[] ActiveActivityLevels = ["MODERATELY_ACTIVE", "VERY_ACTIVE"];

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

        var steps = ReadInt(stepsTask.Result, "countSum") ?? 0;
        var distanceMillimeters = ReadDecimal(distanceTask.Result, "millimetersSum") ?? 0;
        var activeMinutes = SumActiveMinutes(activeMinutesTask.Result);
        var calories = ReadInt(caloriesTask.Result, "kcalSum") ?? 0;
        var floors = ReadInt(floorsTask.Result, "countSum") ?? 0;

        // Sedentary minutes are not collected. The `sedentary-period` data type does expose them
        // (as a `durationSum` in seconds), but nothing consumes them yet — so report null, not 0:
        // the multi-device merge coalesces on the first non-null value, and a placeholder 0 from a
        // higher-priority device would beat another device's genuine reading.
        return new FitbitActivitiesResult(
            steps, decimal.Round(distanceMillimeters / 1_000_000m, 3), activeMinutes, null, floors, calories);
    }

    public async Task<FitbitHeartRateResult> GetHeartRateAsync(string accessToken, DateOnly date)
    {
        var heartRate = await DailyRollupValueAsync(accessToken, "heart-rate", date);

        var minHr = ReadInt(heartRate, "beatsPerMinuteMin");
        var maxHr = ReadInt(heartRate, "beatsPerMinuteMax");
        var avgHr = ReadInt(heartRate, "beatsPerMinuteAvg");

        // Daily resting HR is a Daily record, so it is listed rather than rolled up. Tolerate its
        // absence rather than failing the whole snapshot — a wearer whose device never derives one
        // is a fact about the device, not an error. A malformed-request 400 is excluded: resting HR
        // anchors the HR baseline, so a bug in the request we build has to surface as a sync error
        // instead of a silent null that quietly degrades the baseline.
        int? restingHr = null;
        try
        {
            var resting = await DailyRecordAsync(
                accessToken, "daily-resting-heart-rate", "dailyRestingHeartRate", date);
            restingHr = ReadInt(resting, "beatsPerMinute");
        }
        catch (FitbitApiException ex) when ((ex.StatusCode is 400 or 404) && !ex.IsMalformedRequest)
        {
        }

        return new FitbitHeartRateResult(restingHr, avgHr, maxHr, minHr);
    }

    public async Task<FitbitSleepResult> GetSleepAsync(string accessToken, DateOnly date)
    {
        // Sleep is session-shaped, so it uses list (get/list are its documented methods) with a
        // civil end-time filter: sessions that ended on the requested date.
        // `civil_end_time`, not `end_time`: the sibling field is a physical instant and demands an
        // RFC-3339 literal, so a bare date against it is a parse failure rather than a coercion.
        // Civil is also the semantics we want — it buckets by the wearer's local day, matching the
        // CivilTimeInterval range every dailyRollUp above uses. Filtering the physical instant
        // would bucket by UTC day, dropping a wearer's late-evening session into tomorrow's
        // snapshot while their steps for the same night stayed in today's.
        var filter = Uri.EscapeDataString(
            $"sleep.interval.civil_end_time >= \"{date:yyyy-MM-dd}\" AND sleep.interval.civil_end_time < \"{date.AddDays(1):yyyy-MM-dd}\"");
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/v4/users/me/dataTypes/sleep/dataPoints?filter={filter}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await _httpClient.SendAsync(request);
        await EnsureSuccessAsync(response);

        var root = await ParseBodyAsync(response, "sleep");
        var sleep = (root["dataPoints"] as JArray)?.OfType<JObject>().FirstOrDefault()?["sleep"];

        DateTime? startTime = null, endTime = null;
        if (DateTime.TryParse(sleep?["interval"]?.Value<string>("startTime"), out var stParsed))
            startTime = stParsed;
        if (DateTime.TryParse(sleep?["interval"]?.Value<string>("endTime"), out var etParsed))
            endTime = etParsed;

        var summary = sleep?["summary"];

        var deep = StageMinutes(summary, "DEEP");
        var light = StageMinutes(summary, "LIGHT");
        var rem = StageMinutes(summary, "REM");
        // A device that does not stage sleep reports ASLEEP instead of DEEP/LIGHT/REM, so the three
        // named stages stay null and this is the only evidence of time asleep. RESTLESS is left out
        // deliberately — Fitbit does not count it as asleep either.
        var asleepStage = StageMinutes(summary, "ASLEEP");
        var awake = ReadInt(summary, "minutesAwake") ?? StageMinutes(summary, "AWAKE");

        var stageTotal = deep.HasValue || light.HasValue || rem.HasValue || asleepStage.HasValue
            ? (deep ?? 0) + (light ?? 0) + (rem ?? 0) + (asleepStage ?? 0)
            : (int?)null;
        var asleepMinutes = ReadInt(summary, "minutesAsleep") ?? stageTotal;

        var totalMinutes = asleepMinutes
            ?? (startTime.HasValue && endTime.HasValue
                // Clamped: a session whose awake minutes exceed its own span is contradictory
                // input, and a negative sleep total would poison the baseline downstream.
                ? Math.Max(0, (int)(endTime.Value - startTime.Value).TotalMinutes - (awake ?? 0))
                : 0);

        // The API exposes no efficiency field, so it is derived the way Fitbit defines it: minutes
        // asleep over minutes in the sleep period. Null unless both are known — a fabricated 100%
        // would feed the 1-5 quality bucket a score no measurement supports.
        var sleepPeriodMinutes = ReadInt(summary, "minutesInSleepPeriod");
        var efficiency = asleepMinutes.HasValue && sleepPeriodMinutes > 0
            ? Math.Clamp((int)decimal.Round(asleepMinutes.Value * 100m / sleepPeriodMinutes.Value), 0, 100)
            : (int?)null;

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
    /// Only Interval and Sample data types support this method.
    /// </summary>
    private async Task<JToken?> DailyRollupValueAsync(string accessToken, string dataType, DateOnly date)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/v4/users/me/dataTypes/{dataType}/dataPoints:dailyRollUp");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var body = new JObject
        {
            // Closed-open CivilTimeInterval covering the single requested day.
            ["range"] = new JObject
            {
                ["start"] = CivilDateTime(date),
                ["end"] = CivilDateTime(date.AddDays(1)),
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

    /// <summary>
    /// GETs the data point of a **Daily** data type for one civil date and returns its value
    /// object, or null when the day has none. Daily types are already one-per-day, so they carry no
    /// rollup at all: <c>dataPoints:dailyRollUp</c> rejects them with an INVALID_ARGUMENT naming
    /// the type, and they are read through <c>list</c> filtered on their own <c>date</c> field.
    /// The union member is not derivable from the type name (<c>daily-resting-heart-rate</c> rolls
    /// up under <c>restingHeartRatePersonalRange</c> elsewhere), so callers name it.
    /// </summary>
    private async Task<JToken?> DailyRecordAsync(
        string accessToken, string dataType, string unionMember, DateOnly date)
    {
        // A Daily record's date is a google.type.Date, filtered with the same closed-open ISO
        // literal bounds the sleep session filter uses.
        var filter = Uri.EscapeDataString(
            $"{unionMember}.date >= \"{date:yyyy-MM-dd}\" AND {unionMember}.date < \"{date.AddDays(1):yyyy-MM-dd}\"");
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/v4/users/me/dataTypes/{dataType}/dataPoints?filter={filter}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await _httpClient.SendAsync(request);
        await EnsureSuccessAsync(response);

        var root = await ParseBodyAsync(response, dataType);
        return (root["dataPoints"] as JArray)?.OfType<JObject>().FirstOrDefault()?[unionMember];
    }

    /// <summary>
    /// A `CivilDateTime` — a `google.type.Date` under `date`, never year/month/day inline. `time`
    /// is omitted, which the API reads as midnight: exactly the day boundary a rollup range wants.
    /// </summary>
    private static JObject CivilDateTime(DateOnly date) => new()
    {
        ["date"] = new JObject
        {
            ["year"] = date.Year,
            ["month"] = date.Month,
            ["day"] = date.Day,
        },
    };

    /// <summary>kebab-case data type name → camelCase union member ("heart-rate" → "heartRate").</summary>
    private static string ToCamelCase(string dataType)
    {
        var parts = dataType.Split('-');
        return parts[0] + string.Concat(parts.Skip(1).Select(p =>
            char.ToUpperInvariant(p[0]) + p[1..]));
    }

    /// <summary>
    /// Total minutes across the activity levels that count as active. `active-minutes` is the one
    /// rollup with no scalar total — it comes back as a per-activity-level breakdown.
    /// </summary>
    private static int SumActiveMinutes(JToken? value)
    {
        if (value?["activeMinutesRollupByActivityLevel"] is not JArray levels)
            return 0;

        var minutes = levels
            .OfType<JObject>()
            .Where(level => ActiveActivityLevels.Contains(level.Value<string>("activityLevel")))
            .Sum(level => ReadDecimal(level, "activeMinutesSum") ?? 0);
        return (int)decimal.Round(minutes);
    }

    /// <summary>
    /// Minutes for one sleep stage. `summary.stagesSummary` is a list keyed by stage type rather
    /// than an object with a field per stage, so a stage the device never recorded is an absent
    /// entry — null, not zero.
    /// </summary>
    private static int? StageMinutes(JToken? summary, string stageType)
    {
        var stage = (summary?["stagesSummary"] as JArray)?
            .OfType<JObject>()
            .FirstOrDefault(s => string.Equals(s.Value<string>("type"), stageType, StringComparison.Ordinal));
        return ReadInt(stage, "minutes");
    }

    private static int? ReadInt(JToken? obj, string name)
    {
        var value = ReadDecimal(obj, name);
        return value.HasValue ? (int)decimal.Round(value.Value) : null;
    }

    private static decimal? ReadDecimal(JToken? obj, string name)
    {
        if (obj is not JObject o)
            return null;

        return o[name] switch
        {
            JValue { Type: JTokenType.Integer or JTokenType.Float } v => v.Value<decimal>(),

            // int64 fields cross the wire as JSON *strings* under proto3 JSON — every count, sum
            // and stage duration here is one. A numeric-only check reads them all as absent, which
            // looks exactly like a wearer with no data.
            JValue { Type: JTokenType.String } s
                when decimal.TryParse(
                    s.Value<string>(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
                => parsed,

            _ => null,
        };
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
                $"Google Health API returned {(int)response.StatusCode}: {body}",
                IsMalformedRequest((int)response.StatusCode, body));
        }
    }

    /// <summary>
    /// A payload the API could not bind comes back as 400 with a `google.rpc.BadRequest` detail
    /// listing field violations. Recognising that shape is what separates "this request is wrong"
    /// from "this account has no such data", which some callers tolerate. Parsed best-effort: an
    /// unparseable or differently-shaped error body is not treated as a request bug.
    /// </summary>
    private static bool IsMalformedRequest(int statusCode, string body)
    {
        if (statusCode != 400 || !JsonUtility.TryParse(body, out var root, out _))
            return false;

        return (root?["error"]?["details"] as JArray)?
            .OfType<JObject>()
            .Any(detail =>
                detail.Value<string>("@type")?.EndsWith("google.rpc.BadRequest", StringComparison.Ordinal) == true
                && detail["fieldViolations"] is JArray { Count: > 0 }) == true;
    }
}
