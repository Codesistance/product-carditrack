using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using CardiTrack.Shared.Json;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CardiTrack.Infrastructure.ExternalClients;

/// <summary>
/// Google Health API v4 client — serves every DeviceType mapped to HealthApi.GoogleHealth in the
/// DeviceProviders configuration (Fitbit devices and Pixel Watch alike; the legacy Fitbit Web API
/// is decommissioned September 2026).
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
/// Every field name and enum member below is checked against the v4 discovery document
/// (`https://health.googleapis.com/$discovery/rest?version=v4`), which is the machine-readable
/// schema and so settles spelling in a way the prose reference cannot. Rollup values are named
/// `{field}{Aggregation}` in camelCase (`countSum`, `beatsPerMinuteAvg`) — an earlier snake_case
/// reading of that convention (`count`, `beatsPerMinute_avg`) matched nothing and silently
/// reported zeros. Units are the schema's own: distance in millimetres, calories in kcal.
/// </para>
/// <para>
/// Two wire encodings hide behind those names and both fail silently if missed: `int64` fields
/// cross the wire as JSON *strings* under proto3 JSON (`"countSum": "9423"`), and `Duration`
/// fields as strings with an `s` suffix (`"durationSum": "28800s"`). A numeric-only parse reads
/// either as absent, which is indistinguishable from a wearer with no data. Check the discovery
/// document's `format` before adding a field: `int64`, `google-duration` and `double` all present
/// as "a number" in an example payload and are three different parses.
/// </para>
/// <para>
/// `filter` expressions are the one place that convention does not hold: their member paths are
/// snake_case throughout — the data type (`daily_resting_heart_rate`) and the field
/// (`civil_end_time`) alike — not the camelCase the JSON response is keyed by.
/// </para>
/// </summary>
public class GoogleHealthApiClient : IGoogleHealthApiClient, IDeviceApiClient
{
    /// <summary>
    /// Activity levels that count as "active minutes", matching Fitbit's classic definition.
    /// `active-minutes` rolls up as a breakdown per level, so leaving LIGHT in would report a
    /// number several times the one wearers see in the Fitbit app.
    /// </summary>
    /// <remarks>
    /// These are the members of <c>ActiveMinutesRollupByActivityLevel.activityLevel</c>, whose
    /// enum is <c>ACTIVITY_LEVEL_UNSPECIFIED | LIGHT | MODERATE | VIGOROUS</c>. Not to be confused
    /// with <c>ActivityLevelRollupByActivityLevelType.activityLevelType</c> — a different data type
    /// (`activity-level`) whose enum really does read
    /// <c>SEDENTARY | LIGHTLY_ACTIVE | MODERATELY_ACTIVE | VERY_ACTIVE</c>. Borrowing that
    /// spelling here matched no level at all and summed to a silent 0 on every wearer.
    /// </remarks>
    private static readonly string[] ActiveActivityLevels = ["MODERATE", "VIGOROUS"];

    /// <summary>
    /// Points per page when listing a Sample series. The API's own maximum; anything larger is
    /// truncated to it. A day of SpO2 runs to a few hundred readings, so this is one page in
    /// practice and the pagination loop exists for the case where it is not.
    /// </summary>
    private const int SamplePageSize = 10_000;

    /// <summary>
    /// Hard stop on a Sample series. Originally 20,000, sized off an assumed 1-minute heart-rate
    /// cadence (~1,440 points/day) — a live wearer in continuous heart-rate tracking mode disproved
    /// that on 2026-08-10, legitimately exceeding 20,000 points for a single civil day. Raised to
    /// cover even a 1-second cadence (86,400 points/day) with headroom. Reaching it with pages
    /// still outstanding still means the filter is selecting more than the requested day, so the
    /// read throws rather than returning a prefix: statistics over part of a longer window are not
    /// the day's statistics.
    /// </summary>
    private const int SampleSeriesCap = 100_000;

    /// <summary>
    /// Minimum spacing between successive page requests within one series read. The Google Health
    /// API's per-user quota is 300 requests/min (5 QPS) standard, but only 2.5 QPS while the app is
    /// unverified — and a single wearer's daily snapshot already fires ~12 requests at once, over
    /// that ceiling on its own (see the quota note in `data_sync_architecture.md`). Raising
    /// <see cref="SampleSeriesCap"/> lets a high-cadence series page several more times in the same
    /// pull; pacing those extra requests keeps them from stacking further onto that burst. No delay
    /// before the first page of a series — most series are one page, and delaying every read would
    /// slow every sync for a limit only multi-page reads can trip.
    /// </summary>
    private static readonly TimeSpan PageRequestDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// The largest gap between two sedentary intervals that still counts as one unbroken stretch.
    /// A minute-grained device emits touching intervals rather than one long one, and clocks
    /// between them are not exact; two minutes is short enough that a real interruption — standing
    /// up, walking to the kitchen — still breaks the run, which is the whole point of measuring it.
    /// </summary>
    private const int SedentaryJoinToleranceMinutes = 2;

    private readonly HttpClient _httpClient;
    private readonly TimeSpan _pageRequestDelay;
    private readonly ILogger<GoogleHealthApiClient> _logger;

    public GoogleHealthApiClient(
        IHttpClientFactory httpClientFactory,
        ILogger<GoogleHealthApiClient> logger,
        TimeSpan? pageRequestDelay = null)
    {
        if (pageRequestDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(pageRequestDelay), pageRequestDelay, "Page request delay cannot be negative.");

        _httpClient = httpClientFactory.CreateClient("GoogleHealthClient");
        _logger = logger;
        _pageRequestDelay = pageRequestDelay ?? PageRequestDelay;
    }

    public async Task<GoogleHealthActivitiesResult> GetActivitiesAsync(string accessToken, DateOnly date)
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

        // Every one of these is null — never 0 — on a day the type reports nothing. The API's own
        // guidance is explicit that an absent rollup bucket means the device was not worn or has
        // not synced, while a present `"countSum": "0"` is a true zero; the two are different
        // facts and only the second is a measurement. Coalescing here would break both consumers
        // downstream: the multi-device merge takes the first non-null value, so a manufactured 0
        // from a higher-priority device would beat another device's genuine reading, and the
        // baseline averages absent days in as zeros, deflating the very figure inactivity
        // detection compares against. A wearer who has simply not synced would read as a wearer
        // who has stopped moving.
        var steps = ReadInt(stepsTask.Result, "countSum");
        var distanceMillimeters = ReadDecimal(distanceTask.Result, "millimetersSum");
        var activeMinutes = SumActiveMinutes(activeMinutesTask.Result);
        var calories = ReadInt(caloriesTask.Result, "kcalSum");
        var floors = ReadInt(floorsTask.Result, "countSum");

        // `sedentary-period` is an Interval type, so it rolls up like the rest, but its only rollup
        // value is a `durationSum`, and that field is a protobuf Duration rather than a bare
        // number — it crosses the wire as `"28800s"`, seconds with a literal `s` suffix. It is the
        // sole rollup here not already in the unit its column wants.
        // Nearest, not truncated: the sub-minute remainder is roughly uniform, so flooring would
        // under-report by ~30s every single day in the same direction, and this column feeds
        // baselines and trend detection where a standing bias matters more than a ±30s error that
        // averages out. Same conversion idiom as SumActiveMinutes below.
        var sedentarySeconds = ReadDurationSeconds(sedentaryTask.Result, "durationSum");
        var sedentaryMinutes = sedentarySeconds.HasValue
            ? (int)decimal.Round(sedentarySeconds.Value / 60m)
            : (int?)null;

        return new GoogleHealthActivitiesResult(
            steps,
            distanceMillimeters.HasValue
                ? decimal.Round(distanceMillimeters.Value / 1_000_000m, 3)
                : (decimal?)null,
            activeMinutes,
            sedentaryMinutes,
            floors,
            calories);
    }

    public async Task<GoogleHealthHeartRateResult> GetHeartRateAsync(string accessToken, DateOnly date)
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
        catch (GoogleHealthApiException ex) when ((ex.StatusCode is 400 or 404) && !ex.IsMalformedRequest)
        {
        }

        return new GoogleHealthHeartRateResult(restingHr, avgHr, maxHr, minHr);
    }

    public async Task<GoogleHealthSleepResult> GetSleepAsync(string accessToken, DateOnly date)
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

        var startTime = ParseInstantUtc(ReadString(sleep?["interval"], "startTime"));
        var endTime = ParseInstantUtc(ReadString(sleep?["interval"], "endTime"));

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

        // Null when no session was returned at all, rather than 0: "the wearer logged no sleep
        // session" and "the wearer slept zero minutes" are different claims, and only the second
        // is a measurement. A 0 here would enter the sleep baseline as a genuine sleepless night
        // every time the watch was off the wrist or had not yet synced.
        var totalMinutes = asleepMinutes
            ?? (startTime.HasValue && endTime.HasValue
                // Clamped: a session whose awake minutes exceed its own span is contradictory
                // input, and a negative sleep total would poison the baseline downstream.
                ? Math.Max(0, (int)(endTime.Value - startTime.Value).TotalMinutes - (awake ?? 0))
                : (int?)null);

        // The API exposes no efficiency field, so it is derived the way Fitbit defines it: minutes
        // asleep over minutes in the sleep period. Null unless both are known — a fabricated 100%
        // would feed the 1-5 quality bucket a score no measurement supports.
        var sleepPeriodMinutes = ReadInt(summary, "minutesInSleepPeriod");
        var efficiency = asleepMinutes.HasValue && sleepPeriodMinutes > 0
            ? Math.Clamp((int)decimal.Round(asleepMinutes.Value * 100m / sleepPeriodMinutes.Value), 0, 100)
            : (int?)null;

        return new GoogleHealthSleepResult(totalMinutes, efficiency, startTime, endTime, deep, light, rem, awake);
    }

    public async Task<GoogleHealthAdditionalMetricsResult> GetAdditionalMetricsAsync(
        string accessToken, DateOnly date)
    {
        // Sample series, both of them: SpO2 is read for its min/max as well as its mean, and the
        // overnight breathing rate has to pick the night's own summary out of the day's.
        var spO2Task = GetSpO2Async(accessToken, date);
        var overnightBreathingTask = OptionalOvernightBreathingRateAsync(accessToken, date);

        // The four below are Daily records read through `list`, so each is filtered on its own
        // `date` field rather than rolled up.
        var vo2MaxTask = OptionalDailyValueAsync(
            accessToken, "daily-vo2-max", "dailyVo2Max", "vo2Max", date);
        // RMSSD rather than the record's deep-sleep or entropy siblings: it is the figure the
        // design's SSA mapping names (docs/llm_design.md), and the one every published HRV
        // yardstick is written in.
        var heartRateVariabilityTask = OptionalDailyValueAsync(
            accessToken,
            "daily-heart-rate-variability",
            "dailyHeartRateVariability",
            "averageHeartRateVariabilityMilliseconds",
            date);
        var breathingRateTask = OptionalDailyValueAsync(
            accessToken, "daily-respiratory-rate", "dailyRespiratoryRate", "breathsPerMinute", date);
        // Wrist wearables do not measure core body temperature; what Fitbit and Pixel Watch derive
        // is this nightly skin figure. It is clinically meaningful only as a deviation from the
        // wearer's own baseline, so the record's baseline and 30-day variation are read alongside
        // the nightly value in a single fetch rather than three.
        var temperatureTask = OptionalDailyTemperatureAsync(accessToken, date);
        await Task.WhenAll(
            spO2Task, vo2MaxTask, breathingRateTask, temperatureTask, heartRateVariabilityTask,
            overnightBreathingTask);

        var (spO2Average, spO2Min, spO2Max) = spO2Task.Result;
        var (nightlyTemperature, temperatureBaseline, temperatureVariation) = temperatureTask.Result;

        return new GoogleHealthAdditionalMetricsResult(
            spO2Average,
            spO2Min,
            spO2Max,
            vo2MaxTask.Result,
            breathingRateTask.Result,
            nightlyTemperature,
            temperatureBaseline,
            temperatureVariation,
            heartRateVariabilityTask.Result is { } hrv ? decimal.Round(hrv, 1) : null,
            overnightBreathingTask.Result);
    }

    /// <summary>
    /// The night's average respiratory rate, from the <c>respiratory-rate-sleep-summary</c> Sample
    /// record's whole-sleep statistics.
    /// </summary>
    /// <remarks>
    /// Distinct from <c>daily-respiratory-rate</c>, which this client already reads: that one
    /// averages the whole day, where a stair climb and a nap pull in opposite directions. The
    /// overnight figure is measured over hours of stillness, which is what makes a rise across
    /// nights legible at all — it is the form every clinical rule of thumb about breathing rate is
    /// written in.
    /// <para>
    /// The record also carries per-stage statistics (deep, light, REM) and a signal-to-noise
    /// figure. Only the whole-sleep average is stored: the stage split is a sleep-architecture
    /// question this product does not ask, and a confidence figure with no reader is a column that
    /// would only ever be written. The stage records stay available on the same fetch if a later
    /// pass wants them.
    /// </para>
    /// </remarks>
    private async Task<decimal?> OptionalOvernightBreathingRateAsync(string accessToken, DateOnly date)
    {
        const string dataType = "respiratory-rate-sleep-summary";
        try
        {
            var points = await ListDataPointsAsync(
                accessToken, dataType, SampleDayFilter(dataType, date), date);

            // The *earliest* summary stamped on this civil day, which is the night: a night is
            // stamped when its sleep session ended, in the morning, while a nap later the same day
            // is stamped in the afternoon. Selected by comparing the timestamps rather than by
            // taking an end of the list — the API documents its ordering as descending, so the
            // first element is the nap, which is what this read took until it was corrected.
            var summaries = points
                .Select(point => point["respiratoryRateSleepSummary"])
                .Where(summary => summary?["fullSleepStats"] is not null)
                .Select(summary => (
                    Summary: summary,
                    At: ParseInstantUtc(ReadString(summary?["sampleTime"], "physicalTime"))))
                .ToList();

            var stats = (summaries.Any(x => x.At.HasValue)
                    ? summaries.Where(x => x.At.HasValue).MinBy(x => x.At!.Value).Summary
                    // No parsable timestamps: fall back to the last element, which under the
                    // documented descending order is the earliest — the same answer, less safely.
                    : summaries.LastOrDefault().Summary)
                ?["fullSleepStats"];

            return ReadDecimal(stats, "breathsPerMinute") is { } breaths
                ? decimal.Round(breaths, 1)
                : null;
        }
        catch (GoogleHealthApiException ex) when (IsAbsentDataType(ex))
        {
            return null;
        }
    }

    /// <summary>
    /// How the day's heart rate was distributed across the wearer's own effort zones, the bpm at
    /// which their moderate zone starts, and the longest unbroken stretch their device recorded
    /// them as sedentary.
    /// </summary>
    /// <remarks>
    /// Three reads that answer one question the daily totals cannot: not how much the wearer moved,
    /// but how hard their heart worked and how long it went unbroken between movements. Steps say
    /// a day was quiet; zone minutes say whether the heart agreed.
    /// <para>
    /// The zone thresholds come from <c>daily-heart-rate-zones</c> rather than being derived here.
    /// They are Karvonen figures computed against the wearer's own resting rate and age, so
    /// re-deriving them would put CardiTrack's arithmetic behind a number the wearer's watch
    /// already shows them — and the two would disagree the moment either changed its formula.
    /// </para>
    /// </remarks>
    /// <param name="sleepWindow">
    /// The night's sleep session, when the caller has read it. Sedentary intervals overlapping it
    /// are excluded before the longest stretch is measured — without that, the post-midnight half
    /// of the night is the longest unbroken sedentary run on almost every day, and the reading
    /// would describe the member asleep while calling itself a daytime rest. Null leaves the whole
    /// civil day in scope, which is only correct for a caller that has no sleep session to give.
    /// </param>
    public async Task<GoogleHealthExertionResult> GetExertionAsync(
        string accessToken, DateOnly date, (DateTime Start, DateTime End)? sleepWindow = null)
    {
        var zoneMinutesTask = OptionalZoneMinutesAsync(accessToken, date);
        var zoneFloorTask = OptionalModerateZoneFloorAsync(accessToken, date);
        var sedentaryStretchTask = OptionalLongestSedentaryStretchAsync(accessToken, date, sleepWindow);
        await Task.WhenAll(zoneMinutesTask, zoneFloorTask, sedentaryStretchTask);

        var (light, moderate, vigorous, peak) = zoneMinutesTask.Result;
        var (stretchMinutes, stretchStart) = sedentaryStretchTask.Result;

        return new GoogleHealthExertionResult(
            light, moderate, vigorous, peak, zoneFloorTask.Result, stretchMinutes, stretchStart);
    }

    /// <summary>
    /// Minutes in each heart-rate zone, from the <c>time-in-heart-rate-zone</c> rollup. Every zone
    /// is null when the day has no rollup at all, and a zone absent from a rollup that does exist
    /// is a real zero — the wearer was measured and never reached it.
    /// </summary>
    private async Task<(int? Light, int? Moderate, int? Vigorous, int? Peak)> OptionalZoneMinutesAsync(
        string accessToken, DateOnly date)
    {
        try
        {
            var rollup = await DailyRollupValueAsync(accessToken, "time-in-heart-rate-zone", date);
            if (rollup?["timeInHeartRateZones"] is not JArray zones)
                return (null, null, null, null);

            int? MinutesIn(string zone)
            {
                var entry = zones
                    .OfType<JObject>()
                    .FirstOrDefault(z => string.Equals(
                        ReadString(z, "heartRateZone"), zone, StringComparison.Ordinal));

                // A zone the day never reached is absent from the list rather than present at
                // zero, and the wearer was measured either way — so a present rollup means 0, not
                // "not measured". `duration` is a protobuf Duration ("1800s"), like sedentary-period.
                var seconds = ReadDurationSeconds(entry, "duration") ?? 0m;
                return (int)decimal.Round(seconds / 60m);
            }

            return (MinutesIn("LIGHT"), MinutesIn("MODERATE"), MinutesIn("VIGOROUS"), MinutesIn("PEAK"));
        }
        catch (GoogleHealthApiException ex) when (IsAbsentDataType(ex))
        {
            return (null, null, null, null);
        }
    }

    /// <summary>
    /// The bpm at which this wearer's <c>MODERATE</c> zone begins — the one threshold of the four
    /// that a caregiver's copy needs, because it is the line between "moving about" and "their
    /// heart is working".
    /// </summary>
    private async Task<int?> OptionalModerateZoneFloorAsync(string accessToken, DateOnly date)
    {
        try
        {
            var record = await DailyRecordAsync(
                accessToken, "daily-heart-rate-zones", "dailyHeartRateZones", date);
            if (record?["heartRateZones"] is not JArray zones)
                return null;

            var moderate = zones
                .OfType<JObject>()
                .FirstOrDefault(z => string.Equals(
                    ReadString(z, "heartRateZoneType"), "MODERATE", StringComparison.Ordinal));

            return ReadInt(moderate, "minBeatsPerMinute");
        }
        catch (GoogleHealthApiException ex) when (IsAbsentDataType(ex))
        {
            return null;
        }
    }

    /// <summary>
    /// The longest unbroken sedentary stretch of the day and when it started, from the
    /// <c>activity-level</c> interval series.
    /// </summary>
    /// <remarks>
    /// This is the one reading here that cannot be derived from a daily total. CardiTrack already
    /// stores <c>SedentaryMinutes</c> from the <c>sedentary-period</c> rollup, but a day of
    /// six hours' stillness broken into twelve half-hours and a day with one unbroken six-hour
    /// stretch sum to the same number and are not the same day — only the second is the shape a
    /// family would want to hear about. Reading the intervals rather than the rollup is what makes
    /// the difference visible.
    /// <para>
    /// Adjacent intervals are joined across gaps up to <see cref="SedentaryJoinToleranceMinutes"/>,
    /// because a device that records level per minute emits a run of touching intervals rather than
    /// one long one, and a strict equality test would report the longest stretch as one minute.
    /// </para>
    /// <para>
    /// <b>No sleep window, no reading.</b> A sleeping wearer is a sedentary wearer and the civil
    /// day opens at midnight, so measuring the whole day would make the small hours the longest
    /// unbroken run on almost every day — and <c>daytime_inactivity_block</c>, whose floor is three
    /// hours, a rule that pages a family about an ordinary night's sleep. Rather than report a
    /// figure we cannot tell from a night, we report none: the alert rule already treats a missing
    /// reading as "could not judge", which is exactly what this is.
    /// </para>
    /// <para>
    /// One bound remains, and it is deliberate. The night that <em>begins</em> on this day belongs
    /// to tomorrow's row and is not fetched here, so an early bedtime leaves a tail of stillness
    /// between it and midnight inside the day's own intervals. It is capped by definition — at most
    /// bedtime to midnight — and unlike the small hours it is time the wearer spent settled while
    /// the day was still theirs, which is nearer what the reading is for than it is to sleep.
    /// </para>
    /// </remarks>
    private async Task<(int? Minutes, DateTime? StartUtc)> OptionalLongestSedentaryStretchAsync(
        string accessToken, DateOnly date, (DateTime Start, DateTime End)? sleepWindow)
    {
        if (sleepWindow is null)
            return (null, null);

        const string dataType = "activity-level";
        try
        {
            var points = await ListDataPointsAsync(
                accessToken, dataType, IntervalDayFilter(dataType, date), date);

            var sedentary = points
                .Select(point => point["activityLevel"])
                .Where(level => string.Equals(
                    ReadString(level, "activityLevelType"), "SEDENTARY", StringComparison.Ordinal))
                .Select(level => (
                    Start: ParseInstantUtc(ReadString(level?["interval"], "startTime")),
                    End: ClipToCivilDayEnd(
                        ParseInstantUtc(ReadString(level?["interval"], "endTime")),
                        level?["interval"]?["civilEndTime"],
                        date)))
                .Where(i => i.Start.HasValue && i.End.HasValue && i.End > i.Start)
                .Select(i => (Start: i.Start!.Value, End: i.End!.Value))
                .SelectMany(i => OutsideSleep(i, sleepWindow.Value))
                .OrderBy(i => i.Start)
                .ToList();

            if (sedentary.Count == 0)
                return (null, null);

            var tolerance = TimeSpan.FromMinutes(SedentaryJoinToleranceMinutes);
            var runStart = sedentary[0].Start;
            var runEnd = sedentary[0].End;
            var longest = (Start: runStart, Length: runEnd - runStart);

            foreach (var (start, end) in sedentary.Skip(1))
            {
                if (start - runEnd <= tolerance)
                {
                    // Overlapping or touching: extend, never shorten — an interval wholly inside
                    // the run must not pull its end backwards.
                    runEnd = end > runEnd ? end : runEnd;
                }
                else
                {
                    runStart = start;
                    runEnd = end;
                }

                if (runEnd - runStart > longest.Length)
                    longest = (runStart, runEnd - runStart);
            }

            return ((int)Math.Round(longest.Length.TotalMinutes), longest.Start);
        }
        catch (GoogleHealthApiException ex) when (IsAbsentDataType(ex))
        {
            return (null, null);
        }
    }

    /// <summary>
    /// The parts of a sedentary interval that fall outside the night's sleep: none where it sits
    /// wholly inside, one where it overlaps an edge, and two where it spans the whole night.
    /// </summary>
    /// <remarks>
    /// A sleeping wearer is a sedentary wearer, and the civil day opens at midnight, so without
    /// this the longest unbroken sedentary run is the small hours on essentially every day —
    /// making "Long daytime rest" a rule about sleep, and its baseline a seven-hour figure nothing
    /// could ever exceed. Both fragments of a spanning interval are kept rather than the longer
    /// one: an evening in a chair and a morning in one are separate rests, and picking between
    /// them here would silently discard whichever the wearer had less of.
    /// </remarks>
    private static IEnumerable<(DateTime Start, DateTime End)> OutsideSleep(
        (DateTime Start, DateTime End) interval, (DateTime Start, DateTime End) sleep)
    {
        if (interval.End <= sleep.Start || interval.Start >= sleep.End)
        {
            yield return interval;
            yield break;
        }

        if (interval.Start < sleep.Start)
            yield return (interval.Start, sleep.Start);

        if (interval.End > sleep.End)
            yield return (sleep.End, interval.End);
    }

    public async Task<DeviceHealthSnapshot> GetHealthSnapshotAsync(string accessToken, DateOnly date)
    {
        var activitiesTask = GetActivitiesAsync(accessToken, date);
        var heartRateTask = GetHeartRateAsync(accessToken, date);
        var sleepTask = GetSleepAsync(accessToken, date);
        var additionalTask = GetAdditionalMetricsAsync(accessToken, date);

        // Exertion is the one read that depends on another: the longest sedentary stretch is a
        // *daytime* figure, and only the sleep session says which hours were the night. Awaited
        // here rather than fetched twice, and everything else stays concurrent around it.
        var sleep = await sleepTask;
        var sleepWindow = sleep.SleepStartTime is { } sleepStart && sleep.SleepEndTime is { } sleepEnd
            ? (sleepStart, sleepEnd)
            : ((DateTime Start, DateTime End)?)null;
        var exertionTask = GetExertionAsync(accessToken, date, sleepWindow);

        await Task.WhenAll(activitiesTask, heartRateTask, additionalTask, exertionTask);

        var activities = activitiesTask.Result;
        var heartRate = heartRateTask.Result;
        var additional = additionalTask.Result;
        var exertion = exertionTask.Result;

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
            // this API (see GoogleHealthAdditionalMetricsResult), so it is skipped rather than filled.
            SpO2Average: additional.SpO2Average,
            SpO2Min: additional.SpO2Min,
            SpO2Max: additional.SpO2Max,
            VO2Max: additional.VO2Max,
            BreathingRate: additional.BreathingRate,
            Temperature: additional.Temperature,
            TemperatureBaseline: additional.TemperatureBaseline,
            TemperatureVariation: additional.TemperatureVariation,
            HeartRateVariabilityMs: additional.HeartRateVariabilityMs,
            OvernightBreathingRate: additional.OvernightBreathingRate,
            LightZoneMinutes: exertion.LightZoneMinutes,
            ModerateZoneMinutes: exertion.ModerateZoneMinutes,
            VigorousZoneMinutes: exertion.VigorousZoneMinutes,
            PeakZoneMinutes: exertion.PeakZoneMinutes,
            ModerateZoneFloorBpm: exertion.ModerateZoneFloorBpm,
            LongestSedentaryStretchMinutes: exertion.LongestSedentaryStretchMinutes,
            LongestSedentaryStretchStartUtc: exertion.LongestSedentaryStretchStartUtc);
    }

    /// <summary>
    /// The sub-daily series for one civil day: heart rate, SpO2 and heart-rate variability as
    /// timestamped samples, steps and active-zone-minutes as intervals stamped at their start.
    /// Five list calls, sequential —
    /// each can independently page up to <see cref="SampleSeriesCap"/> parsed points for a
    /// high-cadence wearer, and fetching all four concurrently let those buffers stack in memory
    /// at once (root cause of the 2026-08-11 dev worker OOM). This is a background sync, not a
    /// latency-sensitive request, so trading concurrency for a bounded memory footprint is a clean
    /// tradeoff.
    /// </summary>
    /// <remarks>
    /// Field names verified against the v4 discovery document like everything else here:
    /// `heart-rate` is a Sample type carrying `beatsPerMinute` (int64 — a JSON string on the
    /// wire); `steps` an Interval type carrying `count` (int64); `active-zone-minutes` an
    /// Interval type carrying `activeZoneMinutes` (int64) *per heart-rate zone*, so one instant
    /// can appear once per zone and consumers sum them; `oxygen-saturation` a Sample type
    /// carrying `percentage` (double).
    /// <para>
    /// Each series tolerates a wearer whose device records no such data type — granular series
    /// are enrichment over the daily snapshot, and an absent type is a fact about the device. A
    /// malformed-request 400 still throws, for the same reason it does everywhere else in this
    /// client: a bug in a filter built here must surface as a sync error, not read as a device
    /// without sensors forever.
    /// </para>
    /// </remarks>
    public async Task<DeviceGranularDay> GetGranularDayAsync(string accessToken, DateOnly date)
    {
        var heartRate = await OptionalSeriesAsync(() =>
            TimestampedSamplesAsync(accessToken, "heart-rate", "heartRate", "beatsPerMinute", date));
        var steps = await OptionalSeriesAsync(() =>
            IntervalSamplesAsync(accessToken, "steps", "steps", "count", date));
        var activeZoneMinutes = await OptionalSeriesAsync(() =>
            IntervalSamplesAsync(accessToken, "active-zone-minutes", "activeZoneMinutes", "activeZoneMinutes", date));
        var spO2 = await OptionalSeriesAsync(() =>
            TimestampedSamplesAsync(accessToken, "oxygen-saturation", "oxygenSaturation", "percentage", date));
        // RMSSD, the same measure the daily record reports, so the minute series and the nightly
        // figure are the same quantity at two grains rather than two different ones.
        var heartRateVariability = await OptionalSeriesAsync(() =>
            TimestampedSamplesAsync(
                accessToken,
                "heart-rate-variability",
                "heartRateVariability",
                "rootMeanSquareOfSuccessiveDifferencesMilliseconds",
                date));

        var day = new DeviceGranularDay(heartRate, steps, activeZoneMinutes, spO2, heartRateVariability);

        // The shared Empty instance, as the interface contract promises — a record's list
        // properties compare by reference, so distinct "empty" instances would not even be
        // structurally equal to it.
        return day.HasAnyData ? day : DeviceGranularDay.Empty;
    }

    /// <summary>Empty series, rather than a failed day, for a device without the data type.</summary>
    private static async Task<IReadOnlyList<GranularSample>> OptionalSeriesAsync(
        Func<Task<IReadOnlyList<GranularSample>>> read)
    {
        try
        {
            return await read();
        }
        catch (GoogleHealthApiException ex) when (IsAbsentDataType(ex))
        {
            return [];
        }
    }

    /// <summary>
    /// The wearer's public health-user id, from `GET /v4/users/me/identity` (the `Identity`
    /// resource, verified against the discovery document): `healthUserId` is the `users/{user}`
    /// segment that keys webhook subscriptions and notifications. Tolerates absence like the
    /// optional daily metrics — a 404 is a fact about the account, a malformed 400 is a bug here
    /// and throws.
    /// </summary>
    public async Task<string?> GetHealthUserIdAsync(string accessToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v4/users/me/identity");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var probe = new GoogleHealthApiException(
                (int)response.StatusCode,
                $"Google Health API identity returned {(int)response.StatusCode}.",
                IsMalformedRequest((int)response.StatusCode, await response.Content.ReadAsStringAsync()));
            if (IsAbsentDataType(probe))
                return null;
            throw probe;
        }

        var root = await ParseBodyAsync(response, "identity");
        var healthUserId = ReadString(root, "healthUserId");
        return string.IsNullOrWhiteSpace(healthUserId) ? null : healthUserId;
    }

    /// <summary>
    /// The wearables paired to this account, from <c>GET /v4/users/me/pairedDevices</c> — the
    /// provider's own device registry, which is where battery level and status live. Verified
    /// against the v4 discovery document: <c>PairedDevice.batteryLevel</c> is an `int32` and
    /// <c>batteryStatus</c> a string banded <c>High | Medium | Low | Empty</c>.
    /// </summary>
    /// <remarks>
    /// Returns empty rather than throwing when the read is not permitted. Unlike every other call
    /// in this client, this one needs <c>googlehealth.settings.readonly</c>, which connections
    /// authorised before that scope shipped do not carry — so a 403 here is the expected steady
    /// state for existing wearers, not a fault, and must never park a working connection in
    /// SyncError over telemetry the caregiver reads as a nicety. The same tolerance applied to a
    /// health metric would be wrong, which is why this does not reuse
    /// <see cref="IsAbsentDataType"/>: that helper deliberately treats a malformed 400 as a bug,
    /// and this adds 403 on top for the unscoped case alone.
    /// <para>
    /// Callers should still check the connection's granted scopes before calling, the same as
    /// <see cref="GetExerciseSessionsAsync"/> does for the location scope; this tolerance is the
    /// backstop for the window where a stored scope list and the token's real grant disagree.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<PairedDeviceInfo>> GetPairedDevicesAsync(string accessToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v4/users/me/pairedDevices");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var probe = new GoogleHealthApiException(
                (int)response.StatusCode,
                $"Google Health API pairedDevices returned {(int)response.StatusCode}.",
                IsMalformedRequest((int)response.StatusCode, await response.Content.ReadAsStringAsync()));

            // 403: the scope was never granted. 400/404: the account exposes no device registry.
            if (probe.StatusCode == 403 || IsAbsentDataType(probe))
                return [];
            throw probe;
        }

        var root = await ParseBodyAsync(response, "pairedDevices");
        var devices = (root["pairedDevices"] as JArray)?.OfType<JObject>() ?? [];

        return devices
            .Select(device => new PairedDeviceInfo(
                DeviceType: ReadString(device, "deviceType"),
                BatteryLevel: ReadInt(device, "batteryLevel"),
                BatteryStatus: ReadString(device, "batteryStatus"),
                DeviceVersion: ReadString(device, "deviceVersion"),
                LastSyncTimeUtc: ParseInstantUtc(ReadString(device, "lastSyncTime"))))
            .ToList();
    }

    /// <summary>
    /// Exercise sessions for one civil day. <c>exercise</c> is a Session type like <c>sleep</c>,
    /// so it is filtered on its own civil end-time the same way. GPS presence is read from the
    /// session's <c>hasLocationData</c> flag on the union value.
    /// </summary>
    /// <remarks>
    /// Field names here (<c>hasLocationData</c>, the exercise union member) follow the same
    /// naming convention every other data type in this client does (kebab-case type →
    /// camelCase union member), but — unlike the rest of this client — have not yet been
    /// individually confirmed against a live discovery-document response, because the
    /// <c>googlehealth.location.readonly</c> scope this data type needs is not provisioned in
    /// any environment yet (docs/llm_design.md). Re-verify against
    /// <c>https://health.googleapis.com/$discovery/rest?version=v4</c> once that scope is
    /// granted and before this path first runs against a live account — the exact failure mode
    /// this client's own history warns about (silent zeros, not errors) applies here too.
    /// </remarks>
    public async Task<IReadOnlyList<ExerciseSession>> GetExerciseSessionsAsync(
        string accessToken, DateOnly date)
    {
        var filter = Uri.EscapeDataString(
            $"exercise.interval.civil_end_time >= \"{date:yyyy-MM-dd}\" AND exercise.interval.civil_end_time < \"{date.AddDays(1):yyyy-MM-dd}\"");
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/v4/users/me/dataTypes/exercise/dataPoints?filter={filter}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await _httpClient.SendAsync(request);
        await EnsureSuccessAsync(response);

        var root = await ParseBodyAsync(response, "exercise");
        var points = (root["dataPoints"] as JArray)?.OfType<JObject>() ?? [];

        var sessions = new List<ExerciseSession>();
        foreach (var point in points)
        {
            var exercise = point["exercise"];
            var sessionId = ReadString(point, "dataPointId");
            var startTime = ParseInstantUtc(ReadString(exercise?["interval"], "startTime"));
            var endTime = ParseInstantUtc(ReadString(exercise?["interval"], "endTime"));
            var hasGps = ReadBool(exercise, "hasLocationData") ?? false;

            if (sessionId is null || !startTime.HasValue || !endTime.HasValue)
                continue;

            sessions.Add(new ExerciseSession(sessionId, startTime.Value, endTime.Value, hasGps));
        }

        return sessions;
    }

    /// <summary>
    /// The first GPS fix off a session's TCX export
    /// (<c>dataPoints/{sessionId}:exportExerciseTcx?alt=media</c>, per docs/llm_design.md), or
    /// null when the file has no track point carrying a position — a GPS lock that never
    /// acquired is not an error. The raw TCX bytes and every coordinate parsed from them live
    /// only in this method's locals; nothing here returns more than the single point the caller
    /// needs for one environmental lookup.
    /// </summary>
    public async Task<ExerciseGpsPoint?> GetExerciseGpsPointAsync(string accessToken, string sessionId)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/v4/users/me/dataTypes/exercise/dataPoints/{Uri.EscapeDataString(sessionId)}:exportExerciseTcx?alt=media");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await _httpClient.SendAsync(request);
        await EnsureSuccessAsync(response);

        var tcx = await response.Content.ReadAsStringAsync();
        return ParseFirstTrackpoint(tcx);
    }

    /// <summary>
    /// TCX is standard Garmin TrainingCenterDatabase XML; matched by local name rather than a
    /// pinned namespace URI, since the schema version a given export declares is not this
    /// client's concern — only the element shape is.
    /// </summary>
    private static ExerciseGpsPoint? ParseFirstTrackpoint(string tcx)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(tcx);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }

        foreach (var trackpoint in document.Descendants().Where(e => e.Name.LocalName == "Trackpoint"))
        {
            var position = trackpoint.Elements().FirstOrDefault(e => e.Name.LocalName == "Position");
            var latitudeText = position?.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "LatitudeDegrees")?.Value;
            var longitudeText = position?.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "LongitudeDegrees")?.Value;
            var timeText = trackpoint.Elements().FirstOrDefault(e => e.Name.LocalName == "Time")?.Value;

            if (double.TryParse(latitudeText, NumberStyles.Float, CultureInfo.InvariantCulture, out var latitude)
                && double.TryParse(longitudeText, NumberStyles.Float, CultureInfo.InvariantCulture, out var longitude)
                && ParseInstantUtc(timeText) is { } time)
            {
                return new ExerciseGpsPoint(latitude, longitude, time);
            }
        }

        return null;
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
        catch (GoogleHealthApiException ex) when (IsAbsentDataType(ex))
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
        catch (GoogleHealthApiException ex) when (IsAbsentDataType(ex))
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the nightly skin-temperature figure, the wearer's own baseline, and the 30-day
    /// relative variation off one <c>daily-sleep-temperature-derivations</c> record — a single
    /// fetch, since all three fields live on the same Daily record.
    /// </summary>
    private async Task<(decimal? Nightly, decimal? Baseline, decimal? Variation)>
        OptionalDailyTemperatureAsync(string accessToken, DateOnly date)
    {
        try
        {
            var record = await DailyRecordAsync(
                accessToken, "daily-sleep-temperature-derivations", "dailySleepTemperatureDerivations", date);
            return (
                ReadDecimal(record, "nightlyTemperatureCelsius"),
                ReadDecimal(record, "baselineTemperatureCelsius"),
                ReadDecimal(record, "relativeNightlyStddev30dCelsius"));
        }
        catch (GoogleHealthApiException ex) when (IsAbsentDataType(ex))
        {
            return (null, null, null);
        }
    }

    /// <summary>
    /// Whether a failure means "this wearer has no such data type" rather than "this request was
    /// wrong". The optional metrics tolerate the former — most Fitbits derive none of them, and
    /// that is a fact about the device, not an error — but never the latter: a 400 carrying field
    /// violations is a bug in the URL or filter built here, and swallowing it would turn a
    /// permanently broken read into a column that merely looks unsupported forever.
    /// </summary>
    private static bool IsAbsentDataType(GoogleHealthApiException ex) =>
        ex.StatusCode is 400 or 404 && !ex.IsMalformedRequest;

    /// <summary>
    /// Every value of <paramref name="field"/> across a **Sample** data type's points for one civil
    /// day. Values only — the timestamped variant below serves the granular reads.
    /// </summary>
    private async Task<List<decimal>> SampleSeriesAsync(
        string accessToken, string dataType, string unionMember, string field, DateOnly date)
    {
        var points = await ListDataPointsAsync(accessToken, dataType, SampleDayFilter(dataType, date), date);
        return points
            .Select(point => ReadDecimal(point[unionMember], field))
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .ToList();
    }

    /// <summary>
    /// Timestamped readings of a **Sample** data type for one civil day — the granular series the
    /// minute-grid substrate stores. The instant is <c>sampleTime.physicalTime</c> (RFC-3339,
    /// parsed to UTC): the civil sibling is output-only presentation, and hour vectors bucket by
    /// UTC. A point missing its time or value is skipped — the schema marks both Required, so an
    /// absence is a malformed point, and a reading that cannot be placed on the clock cannot be
    /// bucketed either.
    /// </summary>
    private async Task<IReadOnlyList<GranularSample>> TimestampedSamplesAsync(
        string accessToken, string dataType, string unionMember, string field, DateOnly date)
    {
        var points = await ListDataPointsAsync(accessToken, dataType, SampleDayFilter(dataType, date), date);
        return points
            .Select(point => ToSample(
                ParseInstantUtc(ReadString(point[unionMember]?["sampleTime"], "physicalTime")),
                ReadDecimal(point[unionMember], field)))
            .OfType<GranularSample>()
            .ToList();
    }

    /// <summary>
    /// Timestamped readings of an **Interval** data type for one civil day, stamped at each
    /// interval's <c>interval.startTime</c>. Filtered on
    /// <c>{data_type}.interval.civil_start_time</c> — the interval twin of the sample filter, same
    /// civil-day semantics.
    /// </summary>
    private async Task<IReadOnlyList<GranularSample>> IntervalSamplesAsync(
        string accessToken, string dataType, string unionMember, string field, DateOnly date)
    {
        var points = await ListDataPointsAsync(accessToken, dataType, IntervalDayFilter(dataType, date), date);
        return points
            .Select(point => ToSample(
                ParseInstantUtc(ReadString(point[unionMember]?["interval"], "startTime")),
                ReadDecimal(point[unionMember], field)))
            .OfType<GranularSample>()
            .ToList();
    }

    private static GranularSample? ToSample(DateTime? timeUtc, decimal? value) =>
        timeUtc.HasValue && value.HasValue
            ? new GranularSample(timeUtc.Value, (float)value.Value)
            : null;

    private static string SampleDayFilter(string dataType, DateOnly date)
    {
        var member = ToSnakeCase(dataType);
        return $"{member}.sample_time.civil_time >= \"{date:yyyy-MM-dd}\" AND {member}.sample_time.civil_time < \"{date.AddDays(1):yyyy-MM-dd}\"";
    }

    private static string IntervalDayFilter(string dataType, DateOnly date)
    {
        var member = ToSnakeCase(dataType);
        return $"{member}.interval.civil_start_time >= \"{date:yyyy-MM-dd}\" AND {member}.interval.civil_start_time < \"{date.AddDays(1):yyyy-MM-dd}\"";
    }

    /// <summary>
    /// Lists a data type's points for one civil day, following pagination. The one paginated read
    /// in this client — every series projection above goes through it.
    /// </summary>
    /// <remarks>
    /// Filters are civil, matching the wearer's local day the way the rollup ranges and the sleep
    /// filter do, so a night's readings are not split across two UTC days.
    /// <para>
    /// Pagination is followed rather than assumed away: the response caps at
    /// <see cref="SamplePageSize"/> points and a silently dropped tail would understate a maximum
    /// and overstate a minimum. Past <see cref="SampleSeriesCap"/> the read throws instead of
    /// looping on or returning early — a civil day cannot legitimately hold that many readings,
    /// so the request is selecting more than one day, and neither more pages nor a truncated
    /// series would yield the day's figures. Stopping at the cap and returning what we have would
    /// report statistics over an arbitrary prefix of a longer window as the day's figures — wrong
    /// data, silently, for as long as the cause persists.
    /// </para>
    /// <para>
    /// Every page after the first waits <see cref="_pageRequestDelay"/> first — see that field's
    /// remarks for why a multi-page series paces itself against the per-user quota.
    /// </para>
    /// </remarks>
    private async Task<List<JObject>> ListDataPointsAsync(
        string accessToken, string dataType, string filter, DateOnly date)
    {
        var escapedFilter = Uri.EscapeDataString(filter);

        var points = new List<JObject>();
        string? pageToken = null;
        do
        {
            if (pageToken is not null)
                await Task.Delay(_pageRequestDelay);

            var url =
                $"/v4/users/me/dataTypes/{dataType}/dataPoints?pageSize={SamplePageSize}&filter={escapedFilter}";
            if (!string.IsNullOrEmpty(pageToken))
                url += $"&pageToken={Uri.EscapeDataString(pageToken)}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await _httpClient.SendAsync(request);
            await EnsureSuccessAsync(response);

            var root = await ParseBodyAsync(response, dataType);
            points.AddRange((root["dataPoints"] as JArray)?.OfType<JObject>() ?? []);

            pageToken = ReadString(root, "nextPageToken");

            if (!string.IsNullOrEmpty(pageToken) && points.Count >= SampleSeriesCap)
            {
                throw new GoogleHealthApiException(
                    0,
                    $"Google Health API {dataType} returned more than {SampleSeriesCap} points for "
                    + $"{date:yyyy-MM-dd} and still had pages outstanding. A single civil day cannot "
                    + "hold that many readings, so the request is selecting more than one day.");
            }
        }
        while (!string.IsNullOrEmpty(pageToken));

        return points;
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
    /// <remarks>
    /// Null when the day carries no breakdown at all, for the same reason the scalar rollups stay
    /// null: an unworn device is not a still one. A breakdown that exists but lists only LIGHT is a
    /// different matter — the wearer was measured and did nothing qualifying — so that is a real 0.
    /// </remarks>
    private int? SumActiveMinutes(JToken? value)
    {
        if (value?["activeMinutesRollupByActivityLevel"] is not JArray levels)
            return null;

        var minutes = levels
            .OfType<JObject>()
            .Where(level => ActiveActivityLevels.Contains(ReadString(level, "activityLevel")))
            .Sum(level => ReadDecimal(level, "activeMinutesSum") ?? 0);
        return (int)decimal.Round(minutes);
    }

    /// <summary>
    /// Minutes for one sleep stage. `summary.stagesSummary` is a list keyed by stage type rather
    /// than an object with a field per stage, so a stage the device never recorded is an absent
    /// entry — null, not zero.
    /// </summary>
    private int? StageMinutes(JToken? summary, string stageType)
    {
        var stage = (summary?["stagesSummary"] as JArray)?
            .OfType<JObject>()
            .FirstOrDefault(s => string.Equals(ReadString(s, "type"), stageType, StringComparison.Ordinal));
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
    /// <summary>
    /// Trims an interval's physical end back to the end of the requested civil day.
    /// </summary>
    /// <remarks>
    /// The list filter selects on civil <em>start</em> time, so an interval that begins before local
    /// midnight and runs past it arrives whole. Left whole it counts as this day's own stillness,
    /// and a device that emits one long run from an early bedtime into the small hours would hand
    /// <c>daytime_inactivity_block</c> a night's sleep wearing a daytime rule's name — the same
    /// failure the sleep-window clip prevents, at the other end of the day.
    /// <para>
    /// The overshoot is measured in civil time and subtracted from the UTC instant, because civil
    /// time is the only place the wearer's own midnight is named: this client is never told their
    /// zone. <c>civilEndTime</c> is optional on <c>ObservationTimeInterval</c>; without it the
    /// interval is left alone, since a clip we cannot size is worse than no clip. An interval
    /// spanning a DST change is off by the shift, which is smaller than the tolerance the joining
    /// below already allows for.
    /// </para>
    /// </remarks>
    private DateTime? ClipToCivilDayEnd(DateTime? endUtc, JToken? civilEndTime, DateOnly date)
    {
        if (endUtc is not { } end || ParseCivilDateTime(civilEndTime) is not { } civilEnd)
            return endUtc;

        var dayEnd = date.AddDays(1).ToDateTime(TimeOnly.MinValue);
        return civilEnd > dayEnd ? end - (civilEnd - dayEnd) : end;
    }

    /// <summary>
    /// Reads a civil timestamp off <c>civilStartTime</c>/<c>civilEndTime</c> on
    /// <c>ObservationTimeInterval</c> — <c>{ date: {year,month,day}, time: {hours,minutes[,
    /// seconds]} }</c>, confirmed against a live payload on 2026-08-23. Not the RFC-3339 string
    /// this client originally assumed the field held: that assumption shipped in the same PR as
    /// the rest of this method and only surfaced once <see cref="ReadString"/>'s shape-mismatch
    /// logging existed to catch it — every point in the sweep was hitting it, silently defeating
    /// the day-end clip this exists for (a clip that can't parse its own field falls back to no
    /// clip, per <see cref="ClipToCivilDayEnd"/>'s own remarks — the same fallback an out-of-range
    /// hour or minute here still gets, via the catch below).
    /// </summary>
    private DateTime? ParseCivilDateTime(JToken? civilDateTime)
    {
        if (civilDateTime is not JObject o)
            return null;

        var dateFields = o["date"];
        var timeFields = o["time"];
        if (ReadInt(dateFields, "year") is not { } year
            || ReadInt(dateFields, "month") is not { } month
            || ReadInt(dateFields, "day") is not { } day)
        {
            return null;
        }

        try
        {
            var civilDate = new DateOnly(year, month, day);
            var civilTime = new TimeOnly(
                ReadInt(timeFields, "hours") ?? 0, ReadInt(timeFields, "minutes") ?? 0,
                ReadInt(timeFields, "seconds") ?? 0);
            return civilDate.ToDateTime(civilTime);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static DateTime? ParseInstantUtc(string? value) =>
        DateTime.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? parsed
            : null;

    private int? ReadInt(JToken? obj, string name)
    {
        var value = ReadDecimal(obj, name);
        return value.HasValue ? (int)decimal.Round(value.Value) : null;
    }

    private decimal? ReadDecimal(JToken? obj, string name)
    {
        if (obj is not JObject o)
            return null;

        switch (o[name])
        {
            case JValue { Type: JTokenType.Integer or JTokenType.Float } v:
                return v.Value<decimal>();

            // int64 fields cross the wire as JSON *strings* under proto3 JSON — every count, sum
            // and stage duration here is one. A numeric-only check reads them all as absent, which
            // looks exactly like a wearer with no data.
            case JValue { Type: JTokenType.String } s
                when decimal.TryParse(
                    s.Value<string>(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed):
                return parsed;

            case var unexpected:
                LogShapeMismatch(o, name, unexpected);
                return null;
        }
    }

    /// <summary>
    /// Reads a string field off a JSON object, or null when the field is absent, not a plain
    /// string, or <paramref name="obj"/> itself is not an object. <c>JToken.Value&lt;string&gt;</c>
    /// throws <see cref="InvalidCastException"/> rather than returning null when the field turns
    /// out to hold something other than a string (an unexpected nested object, say) — a live
    /// wearer's <c>activity-level</c> feed hit exactly that on 2026-08-23, taking the whole sync
    /// down. This mirrors <see cref="ReadDecimal"/>'s pattern-match-and-fall-through shape so a
    /// field that does not match the shape we expect reads as absent instead of crashing the sync.
    /// </summary>
    private string? ReadString(JToken? obj, string name)
    {
        if (obj is not JObject o)
            return null;
        if (o[name] is JValue { Type: JTokenType.String } v)
            return v.Value<string>();

        LogShapeMismatch(o, name, o[name]);
        return null;
    }

    /// <summary>Same shape-safety as <see cref="ReadString"/>, for a boolean field.</summary>
    private bool? ReadBool(JToken? obj, string name)
    {
        if (obj is not JObject o)
            return null;
        if (o[name] is JValue { Type: JTokenType.Boolean } v)
            return v.Value<bool>();

        LogShapeMismatch(o, name, o[name]);
        return null;
    }

    /// <summary>
    /// Reads a protobuf <c>Duration</c> field as a number of seconds, or null when absent or
    /// unparseable.
    /// </summary>
    /// <remarks>
    /// Proto3 JSON writes a Duration as a decimal string with a mandatory <c>s</c> suffix —
    /// <c>"28800s"</c>, and fractional for sub-second precision (<c>"1.5s"</c>). Passing that
    /// through <see cref="ReadDecimal"/> fails on the suffix and yields null, which for
    /// `sedentary-period` is indistinguishable from a wearer whose device never reported one. The
    /// suffix is required rather than optional: a bare number is not a valid Duration, so
    /// accepting one would only mask a field that is not the type we think it is.
    /// </remarks>
    private decimal? ReadDurationSeconds(JToken? obj, string name)
    {
        if (obj is not JObject o)
            return null;
        if (o[name] is not JValue { Type: JTokenType.String } value)
        {
            LogShapeMismatch(o, name, o[name]);
            return null;
        }

        var text = value.Value<string>();
        if (text is null || !text.EndsWith('s'))
            return null;

        return decimal.TryParse(
            text.AsSpan(0, text.Length - 1),
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out var seconds)
            ? seconds
            : null;
    }

    /// <summary>
    /// Logs a field whose value is present but not the JSON kind this client expects — a nested
    /// object where a scalar was documented, say. A missing key or a literal JSON <c>null</c> is
    /// not this: that is the ordinary "device does not report this field" case every Read* helper
    /// already treats as absent, and warning on it would fire on nearly every sync. This is the
    /// other case — <c>2026-08-23</c>'s outage was exactly one of these, an
    /// <c>activityLevelType</c> that came back as an object, uncaught, all the way through
    /// <c>Task.WhenAll</c> in <see cref="GetHealthSnapshotAsync"/>. The one-line warning names what
    /// broke at whatever level ships today; the full payload — which can carry health data, so it
    /// is not something every environment should log — only follows at Debug, which only dev turns
    /// on.
    /// </summary>
    private void LogShapeMismatch(JObject container, string field, JToken? actual)
    {
        if (actual is null or JValue { Type: JTokenType.Null })
            return;

        _logger.LogWarning(
            "Google Health API field {Field} was {ActualType}, not the shape this client expects; " +
            "treating it as absent.",
            field, actual.Type);

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "Google Health API field {Field} had an unexpected shape. Payload: {Payload}",
                field, container.ToString(Formatting.None));
        }
    }

    private static async Task<JToken> ParseBodyAsync(HttpResponseMessage response, string what)
    {
        var body = await response.Content.ReadAsStringAsync();
        if (!JsonUtility.TryParse(body, out var root, out var errors))
            throw new GoogleHealthApiException((int)response.StatusCode,
                $"Google Health API {what} response was not valid JSON: {string.Join("; ", errors)}. Payload: {JsonUtility.PreviewOf(body)}");
        return root!;
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new GoogleHealthApiException((int)response.StatusCode,
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
    private bool IsMalformedRequest(int statusCode, string body)
    {
        if (statusCode != 400 || !JsonUtility.TryParse(body, out var root, out _))
            return false;

        return (root?["error"]?["details"] as JArray)?
            .OfType<JObject>()
            .Any(detail =>
                ReadString(detail, "@type")?.EndsWith("google.rpc.BadRequest", StringComparison.Ordinal) == true
                && detail["fieldViolations"] is JArray { Count: > 0 }) == true;
    }
}
