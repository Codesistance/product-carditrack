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
/// Reads follow the method each data type actually supports. Interval types (steps, distance,
/// active-minutes, total-calories, floors, sedentary-period) and Sample types (heart-rate) take
/// `dataPoints:dailyRollUp`; Daily types (daily-resting-heart-rate, daily-oxygen-saturation,
/// daily-vo2-max, daily-respiratory-rate, daily-sleep-temperature-derivations) support only
/// `list`/`reconcile` and 400 on a rollup; Session types (sleep) take `list` with a civil-time
/// filter. A Sample type is also listed directly, rather than rolled up, where the rollup omits an
/// aggregation this client needs — `oxygen-saturation` has no min/max rollup.
/// </para>
/// <para>
/// Every field name below is the one in the v4 reference. Rollup values are named
/// `{field}{Aggregation}` in camelCase (`countSum`, `beatsPerMinuteAvg`) — an earlier snake_case
/// reading of that convention (`count`, `beatsPerMinute_avg`) matched nothing and silently
/// reported zeros. Units are the reference's own: distance in millimetres, calories in kcal.
/// </para>
/// <para>
/// `filter` expressions are the one place that convention does not hold: their member paths are
/// snake_case throughout — the data type (`daily_resting_heart_rate`) and the field
/// (`civil_end_time`) alike — not the camelCase the JSON response is keyed by.
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

    /// <summary>
    /// Points per page when listing a Sample series. The API's own maximum; anything larger is
    /// truncated to it. A day of SpO2 runs to a few hundred readings, so this is one page in
    /// practice and the pagination loop exists for the case where it is not.
    /// </summary>
    private const int SamplePageSize = 10_000;

    /// <summary>
    /// Hard stop on a Sample series, well above the ~1,440 points a once-per-minute sensor can
    /// produce in a civil day. Reaching it means the filter is selecting more than the requested
    /// day, which more pages would compound rather than correct.
    /// </summary>
    private const int SampleSeriesCap = 20_000;

    private readonly HttpClient _httpClient;

    public FitbitApiClient(IHttpClientFactory httpClientFactory)
    {
        _httpClient = httpClientFactory.CreateClient("FitbitClient");
    }

    public async Task<FitbitActivitiesResult> GetActivitiesAsync(string accessToken, DateOnly date)
    {
        // The six rollups are independent — issue them concurrently.
        var stepsTask = DailyRollupValueAsync(accessToken, "steps", date);
        var distanceTask = DailyRollupValueAsync(accessToken, "distance", date);
        var activeMinutesTask = DailyRollupValueAsync(accessToken, "active-minutes", date);
        var caloriesTask = DailyRollupValueAsync(accessToken, "total-calories", date);
        var floorsTask = DailyRollupValueAsync(accessToken, "floors", date);
        var sedentaryTask = DailyRollupValueAsync(accessToken, "sedentary-period", date);
        await Task.WhenAll(
            stepsTask, distanceTask, activeMinutesTask, caloriesTask, floorsTask, sedentaryTask);

        var steps = ReadInt(stepsTask.Result, "countSum") ?? 0;
        var distanceMillimeters = ReadDecimal(distanceTask.Result, "millimetersSum") ?? 0;
        var activeMinutes = SumActiveMinutes(activeMinutesTask.Result);
        var calories = ReadInt(caloriesTask.Result, "kcalSum") ?? 0;
        var floors = ReadInt(floorsTask.Result, "countSum") ?? 0;

        // `sedentary-period` is an Interval type, so it rolls up like the rest, but its only rollup
        // value is a `durationSum` in **seconds** — the sole rollup here not already in the unit its
        // column wants. It stays null rather than 0 on a day the type reports nothing: the
        // multi-device merge coalesces on the first non-null value, so a placeholder 0 from a
        // higher-priority device would beat another device's genuine reading.
        var sedentarySeconds = ReadDecimal(sedentaryTask.Result, "durationSum");
        var sedentaryMinutes = sedentarySeconds.HasValue
            ? (int)decimal.Round(sedentarySeconds.Value / 60m)
            : (int?)null;

        return new FitbitActivitiesResult(
            steps,
            decimal.Round(distanceMillimeters / 1_000_000m, 3),
            activeMinutes,
            sedentaryMinutes,
            floors,
            calories);
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

        var startTime = ParseInstantUtc(sleep?["interval"]?.Value<string>("startTime"));
        var endTime = ParseInstantUtc(sleep?["interval"]?.Value<string>("endTime"));

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

    public async Task<FitbitAdditionalMetricsResult> GetAdditionalMetricsAsync(
        string accessToken, DateOnly date)
    {
        var spO2Task = GetSpO2Async(accessToken, date);
        // Each of the three is a Daily record read through `list`, so each is filtered on its own
        // `date` field rather than rolled up.
        var vo2MaxTask = OptionalDailyValueAsync(
            accessToken, "daily-vo2-max", "dailyVo2Max", "vo2Max", date);
        var breathingRateTask = OptionalDailyValueAsync(
            accessToken, "daily-respiratory-rate", "dailyRespiratoryRate", "breathsPerMinute", date);
        // Wrist wearables do not measure core body temperature; what Fitbit and Pixel Watch derive
        // is this nightly skin figure. `baselineTemperatureCelsius` sits alongside it and is the
        // more clinically useful half — a nightly reading means little except as a deviation from
        // the wearer's own baseline — but ActivityLog has one temperature column, so only the
        // nightly value is kept. Storing the baseline too needs a migration: issue #81.
        var temperatureTask = OptionalDailyValueAsync(
            accessToken,
            "daily-sleep-temperature-derivations",
            "dailySleepTemperatureDerivations",
            "nightlyTemperatureCelsius",
            date);
        await Task.WhenAll(spO2Task, vo2MaxTask, breathingRateTask, temperatureTask);

        var (spO2Average, spO2Min, spO2Max) = spO2Task.Result;

        return new FitbitAdditionalMetricsResult(
            spO2Average,
            spO2Min,
            spO2Max,
            vo2MaxTask.Result,
            breathingRateTask.Result,
            temperatureTask.Result);
    }

    public async Task<DeviceHealthSnapshot> GetHealthSnapshotAsync(string accessToken, DateOnly date)
    {
        var activitiesTask = GetActivitiesAsync(accessToken, date);
        var heartRateTask = GetHeartRateAsync(accessToken, date);
        var sleepTask = GetSleepAsync(accessToken, date);
        var additionalTask = GetAdditionalMetricsAsync(accessToken, date);
        await Task.WhenAll(activitiesTask, heartRateTask, sleepTask, additionalTask);

        var activities = activitiesTask.Result;
        var heartRate = heartRateTask.Result;
        var sleep = sleepTask.Result;
        var additional = additionalTask.Result;

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
            sleep.AwakeMinutes,
            // Named from here on: StressScore sits among these positionally but has no source on
            // this API (see FitbitAdditionalMetricsResult), so it is skipped rather than filled.
            SpO2Average: additional.SpO2Average,
            SpO2Min: additional.SpO2Min,
            SpO2Max: additional.SpO2Max,
            VO2Max: additional.VO2Max,
            BreathingRate: additional.BreathingRate,
            Temperature: additional.Temperature);
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
        //
        // The filter's leading segment is the *data type* in snake_case — the documented pattern is
        // `{daily_summary_data_type}.date` — not the camelCase union member the response is keyed
        // by. The two coincide for `sleep`, which is why the session filter above reads as though
        // either would do; they diverge here, and `dailyRestingHeartRate.date` is rejected with
        // INVALID_DATA_POINT_FILTER_DATA_TYPE_RESTRICTION ("does not match any data type").
        var member = ToSnakeCase(dataType);
        var filter = Uri.EscapeDataString(
            $"{member}.date >= \"{date:yyyy-MM-dd}\" AND {member}.date < \"{date.AddDays(1):yyyy-MM-dd}\"");
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
    /// Daily SpO2, as average, minimum and maximum over the wearer's civil day.
    /// </summary>
    /// <remarks>
    /// All three come from the <c>oxygen-saturation</c> **sample** series rather than the
    /// <c>daily-oxygen-saturation</c> summary, for two reasons. The summary carries no minimum or
    /// maximum at all: its <c>lowerBoundPercentage</c>/<c>upperBoundPercentage</c> pair arrives
    /// beside a <c>standardDeviationPercentage</c> and describes the spread of the day's
    /// distribution, not the lowest reading taken — storing a distribution bound in a column named
    /// <c>SpO2Min</c> would misreport the one figure a desaturation check would look at. And
    /// deriving all three from one series keeps them mutually consistent, which mixing a summary
    /// average with sample extremes would not guarantee.
    /// <para>
    /// The summary is still the fallback when the series is empty: a device that publishes only a
    /// daily average should contribute that average rather than nothing. Minimum and maximum stay
    /// null in that case — there is no honest value for them.
    /// </para>
    /// </remarks>
    private async Task<(decimal? Average, decimal? Min, decimal? Max)> GetSpO2Async(
        string accessToken, DateOnly date)
    {
        try
        {
            var samples = await SampleSeriesAsync(
                accessToken, "oxygen-saturation", "oxygenSaturation", "percentage", date);
            if (samples.Count > 0)
            {
                return (
                    decimal.Round(samples.Average(), 1),
                    decimal.Round(samples.Min(), 1),
                    decimal.Round(samples.Max(), 1));
            }

            var daily = await DailyRecordAsync(
                accessToken, "daily-oxygen-saturation", "dailyOxygenSaturation", date);
            var average = ReadDecimal(daily, "averagePercentage");
            return (average.HasValue ? decimal.Round(average.Value, 1) : null, null, null);
        }
        catch (FitbitApiException ex) when (IsAbsentDataType(ex))
        {
            return (null, null, null);
        }
    }

    /// <summary>
    /// Reads one field off a **Daily** record, returning null when the wearer's device does not
    /// record that data type at all rather than failing the whole day's snapshot.
    /// </summary>
    private async Task<decimal?> OptionalDailyValueAsync(
        string accessToken, string dataType, string unionMember, string field, DateOnly date)
    {
        try
        {
            var record = await DailyRecordAsync(accessToken, dataType, unionMember, date);
            return ReadDecimal(record, field);
        }
        catch (FitbitApiException ex) when (IsAbsentDataType(ex))
        {
            return null;
        }
    }

    /// <summary>
    /// Whether a failure means "this wearer has no such data type" rather than "this request was
    /// wrong". The optional metrics tolerate the former — most Fitbits derive none of them, and
    /// that is a fact about the device, not an error — but never the latter: a 400 carrying field
    /// violations is a bug in the URL or filter built here, and swallowing it would turn a
    /// permanently broken read into a column that merely looks unsupported forever.
    /// </summary>
    private static bool IsAbsentDataType(FitbitApiException ex) =>
        ex.StatusCode is 400 or 404 && !ex.IsMalformedRequest;

    /// <summary>
    /// Every value of <paramref name="field"/> across a **Sample** data type's points for one civil
    /// day, following pagination.
    /// </summary>
    /// <remarks>
    /// A sample type is filtered on <c>{data_type}.sample_time.civil_time</c> — civil, matching the
    /// wearer's local day the way the rollup ranges and the sleep filter do, so a night's readings
    /// are not split across two UTC days.
    /// <para>
    /// Pagination is followed rather than assumed away: the response caps at
    /// <see cref="SamplePageSize"/> points and a silently dropped tail would understate a maximum
    /// and overstate a minimum. <see cref="SampleSeriesCap"/> bounds the loop — a civil day cannot
    /// legitimately hold more readings than that, so hitting it means the filter is matching more
    /// than one day and looping further would not fix it.
    /// </para>
    /// </remarks>
    private async Task<List<decimal>> SampleSeriesAsync(
        string accessToken, string dataType, string unionMember, string field, DateOnly date)
    {
        var member = ToSnakeCase(dataType);
        var filter = Uri.EscapeDataString(
            $"{member}.sample_time.civil_time >= \"{date:yyyy-MM-dd}\" AND {member}.sample_time.civil_time < \"{date.AddDays(1):yyyy-MM-dd}\"");

        var values = new List<decimal>();
        string? pageToken = null;
        do
        {
            var url =
                $"/v4/users/me/dataTypes/{dataType}/dataPoints?pageSize={SamplePageSize}&filter={filter}";
            if (!string.IsNullOrEmpty(pageToken))
                url += $"&pageToken={Uri.EscapeDataString(pageToken)}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await _httpClient.SendAsync(request);
            await EnsureSuccessAsync(response);

            var root = await ParseBodyAsync(response, dataType);
            foreach (var point in (root["dataPoints"] as JArray)?.OfType<JObject>() ?? [])
            {
                if (ReadDecimal(point[unionMember], field) is { } value)
                    values.Add(value);
            }

            pageToken = root.Value<string>("nextPageToken");
        }
        while (!string.IsNullOrEmpty(pageToken) && values.Count < SampleSeriesCap);

        return values;
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

    /// <summary>
    /// kebab-case data type name → the snake_case name the filter grammar uses
    /// ("daily-resting-heart-rate" → "daily_resting_heart_rate").
    /// </summary>
    private static string ToSnakeCase(string dataType) => dataType.Replace('-', '_');

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

    /// <summary>
    /// Reads a physical instant (`interval.startTime`/`endTime`, RFC-3339) as a UTC
    /// <see cref="DateTime"/>, or null when the field is absent or unparseable.
    /// </summary>
    /// <remarks>
    /// The Kind is the point. A bare <c>DateTime.TryParse</c> honours the offset by converting the
    /// instant into the *host machine's* local zone and stamping it <c>Kind=Local</c>, which
    /// Npgsql refuses to write to a <c>timestamp with time zone</c> column — the whole sync day
    /// fails at SaveChanges, not just the sleep fields. <c>AssumeUniversal</c> covers the other
    /// end: an offsetless literal would otherwise be read as local and land as
    /// <c>Kind=Unspecified</c>, rejected for the same column. Both styles together mean every
    /// parse returns <c>Kind=Utc</c> whatever the provider sends, and the instant itself is
    /// unchanged.
    /// </remarks>
    private static DateTime? ParseInstantUtc(string? value) =>
        DateTime.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? parsed
            : null;

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
