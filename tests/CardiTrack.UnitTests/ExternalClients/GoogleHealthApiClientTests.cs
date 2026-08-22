using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using CardiTrack.Infrastructure.ExternalClients;
using NSubstitute;

namespace CardiTrack.UnitTests.ExternalClients;

public class GoogleHealthApiClientTests
{
    /// <summary>
    /// The Google Health API client issues one request per data type, so responses are routed by
    /// path substring. Unmatched dailyRollUp routes return an empty rollup (a day with no data).
    /// </summary>
    private sealed class RoutedFakeHttpHandler : HttpMessageHandler
    {
        private readonly List<(string PathContains, string Body, HttpStatusCode Status)> _routes = [];

        /// <summary>
        /// Guards what the handler records. Snapshot methods read every metric a member needs
        /// concurrently (five rollups under one Task.WhenAll), so SendAsync must assume it is
        /// re-entered on other threads — an unsynchronized List.Add loses entries or throws.
        /// </summary>
        private readonly Lock _recorded = new();

        private readonly List<HttpRequestMessage> _requests = [];

        /// <summary>
        /// Request payloads by path, captured while the request is in flight — the client disposes
        /// each HttpRequestMessage once sent, which disposes its content along with it.
        /// </summary>
        private readonly List<(string Path, string Body)> _sentBodies = [];

        /// <summary>
        /// When each request arrived, relative to handler construction — for asserting the spacing
        /// between two specific requests (e.g. pagination pacing) without relying on the total
        /// wall-clock time of the whole call, which also includes unrelated concurrent work.
        /// </summary>
        private readonly List<(string Path, TimeSpan At)> _timestamps = [];

        private readonly Stopwatch _clock = Stopwatch.StartNew();

        /// <summary>Snapshot, so a caller cannot enumerate the list while a request is in flight.</summary>
        public IReadOnlyList<HttpRequestMessage> Requests
        {
            get { lock (_recorded) return [.. _requests]; }
        }

        /// <summary>Arrival times of requests to <paramref name="pathContains"/>, in request order.</summary>
        public IReadOnlyList<TimeSpan> TimestampsFor(string pathContains)
        {
            lock (_recorded)
                return [.. _timestamps
                    .Where(t => t.Path.Contains(pathContains, StringComparison.Ordinal))
                    .Select(t => t.At)];
        }

        /// <summary>
        /// Successive responses for repeated requests to one path, for the paginated reads. Checked
        /// before <see cref="_routes"/>; over-requesting a sequence answers 500 rather than
        /// repeating its last page, so a pagination loop that fails to terminate fails the test
        /// instead of spinning until the client's own cap.
        /// </summary>
        private readonly List<(string PathContains, Queue<string> Bodies)> _sequences = [];

        public RoutedFakeHttpHandler Map(string pathContains, string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            _routes.Add((pathContains, body, status));
            return this;
        }

        public RoutedFakeHttpHandler MapSequence(string pathContains, params string[] bodies)
        {
            _sequences.Add((pathContains, new Queue<string>(bodies)));
            return this;
        }

        public string BodyFor(string pathContains)
        {
            lock (_recorded)
                return _sentBodies.Single(b => b.Path.Contains(pathContains, StringComparison.Ordinal)).Body;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;

            // Read the content before taking the lock — awaiting inside a lock is not allowed.
            var requestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            lock (_recorded)
            {
                _requests.Add(request);
                _timestamps.Add((path, _clock.Elapsed));
                if (requestBody is not null)
                    _sentBodies.Add((path, requestBody));
            }

            lock (_recorded)
            {
                var sequence = _sequences.FirstOrDefault(s =>
                    path.Contains(s.PathContains, StringComparison.Ordinal));
                if (sequence != default)
                {
                    var (sequenceBody, sequenceStatus) = sequence.Bodies.Count > 0
                        ? (sequence.Bodies.Dequeue(), HttpStatusCode.OK)
                        : ($"sequence for {sequence.PathContains} exhausted",
                            HttpStatusCode.InternalServerError);
                    return new HttpResponseMessage(sequenceStatus)
                    {
                        Content = new StringContent(sequenceBody, Encoding.UTF8, "application/json")
                    };
                }
            }

            var route = _routes.FirstOrDefault(r => path.Contains(r.PathContains, StringComparison.Ordinal));
            var body = route == default ? """{ "rollupDataPoints": [] }""" : route.Body;
            var status = route == default ? HttpStatusCode.OK : route.Status;
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }

    private static (IGoogleHealthApiClient Sut, RoutedFakeHttpHandler Handler) CreateSut(
        RoutedFakeHttpHandler? handler = null, TimeSpan? pageRequestDelay = null)
    {
        handler ??= new RoutedFakeHttpHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://health.googleapis.com") };
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("GoogleHealthClient").Returns(httpClient);
        // Zero inter-page delay by default: production paces pagination against the per-user quota
        // (see GoogleHealthApiClient's PageRequestDelay), but most of these tests assert request count
        // and content, not wall-clock timing, and a real delay would make every multi-page test
        // slow. Tests that specifically exercise pacing pass their own (short) delay.
        return (new GoogleHealthApiClient(factory, pageRequestDelay ?? TimeSpan.Zero), handler);
    }

    /// <summary>
    /// A negative delay would otherwise only surface as a <see cref="Task.Delay(TimeSpan)"/>
    /// exception on a series' second page — deferred, and only for wearers whose data happens to
    /// paginate. Rejecting it at construction fails fast on the misconfiguration itself.
    /// </summary>
    [Fact]
    public void Constructor_Throws_WhenPageRequestDelayIsNegative()
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("GoogleHealthClient").Returns(new HttpClient());

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new GoogleHealthApiClient(factory, TimeSpan.FromMilliseconds(-1)));
    }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    private static string Rollup(string unionMember, string valueJson) => $$"""
        {
          "rollupDataPoints": [
            {
              "civilStartTime": { "year": 2026, "month": 8, "day": 5 },
              "civilEndTime": { "year": 2026, "month": 8, "day": 6 },
              "{{unionMember}}": {{valueJson}}
            }
          ]
        }
        """;

    // ── Activities ──────────────────────────────────────────────────────────────

    /// <summary>
    /// `countSum` is an int64, and proto3 JSON serialises int64 as a *string*. Parsing that only
    /// as a number reads every step count as absent — indistinguishable from a wearer who did not
    /// move, which is why this asserts the quoted form specifically.
    /// </summary>
    [Fact]
    public async Task GetActivitiesAsync_ReturnsSteps_FromDailyRollup()
    {
        var handler = new RoutedFakeHttpHandler()
            .Map("/dataTypes/steps/", Rollup("steps", """{ "countSum": "9423" }"""));

        var (sut, _) = CreateSut(handler);
        var result = await sut.GetActivitiesAsync("token", Today);

        Assert.Equal(9423, result.Steps);
    }

    /// <summary>
    /// Distance rolls up in **millimetres**, not metres — a metres reading of the same number is
    /// off by 1000×, and 6.3 km arriving as 6300 km would clear any plausibility check a reviewer
    /// might apply by eye.
    /// </summary>
    [Fact]
    public async Task GetActivitiesAsync_ConvertsDistanceMillimetersToKm()
    {
        var handler = new RoutedFakeHttpHandler()
            .Map("/dataTypes/distance/", Rollup("distance", """{ "millimetersSum": "6300000" }"""));

        var (sut, _) = CreateSut(handler);
        var result = await sut.GetActivitiesAsync("token", Today);

        Assert.Equal(6.3m, result.DistanceKm);
    }

    [Fact]
    public async Task GetActivitiesAsync_ReturnsCaloriesAndFloors_FromDailyRollup()
    {
        var handler = new RoutedFakeHttpHandler()
            // kcalSum is a double, unlike the int64 sums either side of it.
            .Map("/dataTypes/total-calories/", Rollup("totalCalories", """{ "kcalSum": 2310.4 }"""))
            .Map("/dataTypes/floors/", Rollup("floors", """{ "countSum": "12" }"""));

        var (sut, _) = CreateSut(handler);
        var result = await sut.GetActivitiesAsync("token", Today);

        Assert.Equal(2310, result.CaloriesBurned);
        Assert.Equal(12, result.Floors);
    }

    /// <summary>
    /// active-minutes is the one rollup with no scalar total: it comes back as a breakdown per
    /// activity level. Only MODERATE and VIGOROUS count, matching Fitbit's classic figure —
    /// including LIGHT would report a number several times what the wearer sees in-app.
    /// <para>
    /// The levels are spelled as `ActiveMinutesRollupByActivityLevel.activityLevel` declares them
    /// in the v4 discovery document: <c>LIGHT | MODERATE | VIGOROUS</c>. The neighbouring
    /// `activity-level` data type uses <c>LIGHTLY_ACTIVE | MODERATELY_ACTIVE | VERY_ACTIVE</c>, and
    /// this fixture once carried those instead — a payload the API never sends, which let the
    /// client match no level at all and still pass.
    /// </para>
    /// </summary>
    [Fact]
    public async Task GetActivitiesAsync_SumsActiveMinutes_ForModerateAndVigorousOnly()
    {
        var handler = new RoutedFakeHttpHandler()
            .Map("/dataTypes/active-minutes/", Rollup("activeMinutes", """
                {
                  "activeMinutesRollupByActivityLevel": [
                    { "activityLevel": "LIGHT",    "activeMinutesSum": "180" },
                    { "activityLevel": "MODERATE", "activeMinutesSum": "22"  },
                    { "activityLevel": "VIGOROUS", "activeMinutesSum": "18"  }
                  ]
                }
                """));

        var (sut, _) = CreateSut(handler);
        var result = await sut.GetActivitiesAsync("token", Today);

        Assert.Equal(40, result.ActiveMinutes);
    }

    /// <summary>
    /// Guards the exact regression this fixture used to hide: the `activity-level` spellings are
    /// not the `active-minutes` ones, so a payload using them contributes nothing. If the client
    /// ever accepts both, a wearer's active minutes could be counted twice.
    /// </summary>
    [Fact]
    public async Task GetActivitiesAsync_IgnoresActivityLevelTypeSpellings_WhichBelongToAnotherDataType()
    {
        var handler = new RoutedFakeHttpHandler()
            .Map("/dataTypes/active-minutes/", Rollup("activeMinutes", """
                {
                  "activeMinutesRollupByActivityLevel": [
                    { "activityLevel": "MODERATELY_ACTIVE", "activeMinutesSum": "22" },
                    { "activityLevel": "VERY_ACTIVE",       "activeMinutesSum": "18" }
                  ]
                }
                """));

        var (sut, _) = CreateSut(handler);
        var result = await sut.GetActivitiesAsync("token", Today);

        Assert.Equal(0, result.ActiveMinutes);
    }

    /// <summary>
    /// A breakdown that exists but lists no qualifying level is a real 0 — the wearer was measured
    /// and did nothing moderate or vigorous. That is a different fact from the absent-rollup case
    /// below, and the two must not collapse into one another.
    /// </summary>
    [Fact]
    public async Task GetActivitiesAsync_ReturnsZeroActiveMinutes_WhenBreakdownHasNoQualifyingLevel()
    {
        var handler = new RoutedFakeHttpHandler()
            .Map("/dataTypes/active-minutes/", Rollup("activeMinutes", """
                {
                  "activeMinutesRollupByActivityLevel": [
                    { "activityLevel": "LIGHT", "activeMinutesSum": "180" }
                  ]
                }
                """));

        var (sut, _) = CreateSut(handler);
        var result = await sut.GetActivitiesAsync("token", Today);

        Assert.Equal(0, result.ActiveMinutes);
    }

    /// <summary>
    /// Null, not 0, on a day no rollup bucket comes back. Google's own guidance is that an absent
    /// bucket means the device was not worn or has not synced, while a present `"countSum": "0"` is
    /// a true zero. Coalescing the first into the second is the failure mode this whole product is
    /// most exposed to: the multi-device merge takes the first non-null value, so a manufactured 0
    /// would beat another device's genuine reading, and the baseline would average unsynced days in
    /// as stillness — an unworn watch reading exactly like an elderly wearer who has stopped moving.
    /// </summary>
    [Fact]
    public async Task GetActivitiesAsync_ReturnsNulls_WhenDayHasNoData()
    {
        var (sut, _) = CreateSut(); // every route returns an empty rollup

        var result = await sut.GetActivitiesAsync("token", Today);

        Assert.Null(result.Steps);
        Assert.Null(result.DistanceKm);
        Assert.Null(result.ActiveMinutes);
        Assert.Null(result.Floors);
        Assert.Null(result.CaloriesBurned);
        Assert.Null(result.SedentaryMinutes);
    }

    /// <summary>
    /// A rollup that really does report zero is kept as zero — the wearer wore the device and did
    /// not move. The pair with the test above is the point: absent and zero must stay distinguishable
    /// all the way to the column.
    /// </summary>
    [Fact]
    public async Task GetActivitiesAsync_KeepsExplicitZero_WhichIsAMeasurementNotAnAbsence()
    {
        var handler = new RoutedFakeHttpHandler()
            .Map("/dataTypes/steps/", Rollup("steps", """{ "countSum": "0" }"""));

        var (sut, _) = CreateSut(handler);
        var result = await sut.GetActivitiesAsync("token", Today);

        Assert.Equal(0, result.Steps);
    }

    /// <summary>
    /// `sedentary-period` rolls up a `durationSum` in **seconds** — the one rollup whose unit is not
    /// the unit its column wants. Storing the raw figure would report 8 hours of sitting as 28,800
    /// minutes, i.e. 20 days.
    /// <para>
    /// It is a protobuf `Duration`, not an int64, so proto3 JSON writes it with a mandatory `s`
    /// suffix. This fixture once sent a bare `"28800"`, which no Duration field ever produces — the
    /// client's numeric parse passed the test and returned null against the real API.
    /// </para>
    /// </summary>
    [Fact]
    public async Task GetActivitiesAsync_ConvertsSedentaryDurationSecondsToMinutes()
    {
        var handler = new RoutedFakeHttpHandler()
            .Map("/dataTypes/sedentary-period/", Rollup("sedentaryPeriod", """{ "durationSum": "28800s" }"""));

        var (sut, _) = CreateSut(handler);
        var result = await sut.GetActivitiesAsync("token", Today);

        Assert.Equal(480, result.SedentaryMinutes);
    }

    /// <summary>
    /// Fractional-second Durations are legal proto3 JSON (`"1.5s"`), so the parse must not assume
    /// an integer prefix and drop the value to null.
    /// </summary>
    [Fact]
    public async Task GetActivitiesAsync_ParsesFractionalSedentaryDuration()
    {
        var handler = new RoutedFakeHttpHandler()
            .Map("/dataTypes/sedentary-period/", Rollup("sedentaryPeriod", """{ "durationSum": "28830.5s" }"""));

        var (sut, _) = CreateSut(handler);
        var result = await sut.GetActivitiesAsync("token", Today);

        // 28,830.5s ÷ 60 = 480.508… minutes, which rounds to 481 rather than flooring to 480.
        // Not a midpoint: 480.5 exactly would round to *480* under decimal.Round's default
        // banker's rounding, so a fixture sitting on the halfway mark would assert the opposite.
        Assert.Equal(481, result.SedentaryMinutes);
    }

    /// <summary>
    /// A bare number is not a valid Duration. Accepting one would only mask a field that has turned
    /// out not to be the type we think it is, so it reads as absent rather than as seconds.
    /// </summary>
    [Fact]
    public async Task GetActivitiesAsync_ReturnsNullSedentaryMinutes_WhenDurationLacksSuffix()
    {
        var handler = new RoutedFakeHttpHandler()
            .Map("/dataTypes/sedentary-period/", Rollup("sedentaryPeriod", """{ "durationSum": "28800" }"""));

        var (sut, _) = CreateSut(handler);
        var result = await sut.GetActivitiesAsync("token", Today);

        Assert.Null(result.SedentaryMinutes);
    }

    /// <summary>
    /// Null, not 0, on a day the type reports nothing. The multi-device merge coalesces on the first
    /// non-null value, so a placeholder 0 from a higher-priority device would win over another
    /// device's genuine reading — and 0 sedentary minutes is itself a clinically odd claim.
    /// </summary>
    [Fact]
    public async Task GetActivitiesAsync_ReturnsNullSedentaryMinutes_WhenTypeReportsNothing()
    {
        var (sut, _) = CreateSut(); // every route returns an empty rollup

        var result = await sut.GetActivitiesAsync("token", Today);

        Assert.Null(result.SedentaryMinutes);
    }

    [Fact]
    public async Task GetActivitiesAsync_PostsDailyRollupPerDataType()
    {
        var (sut, handler) = CreateSut();

        await sut.GetActivitiesAsync("token", Today);

        var stepsRequest = handler.Requests.Single(r =>
            r.RequestUri!.AbsolutePath.Contains("/dataTypes/steps/"));
        Assert.Equal(HttpMethod.Post, stepsRequest.Method);
        Assert.EndsWith("dataPoints:dailyRollUp", stepsRequest.RequestUri!.AbsolutePath);
        Assert.Equal("Bearer", stepsRequest.Headers.Authorization!.Scheme);
    }

    [Fact]
    public async Task GetActivitiesAsync_PostsClosedOpenCivilTimeIntervalRange()
    {
        var (sut, handler) = CreateSut();

        // Month end, so the exclusive end also exercises the rollover into the next month.
        await sut.GetActivitiesAsync("token", new DateOnly(2026, 8, 31));

        using var document = JsonDocument.Parse(handler.BodyFor("/dataTypes/steps/"));
        var range = document.RootElement.GetProperty("range");
        AssertCivilDateTime(new DateOnly(2026, 8, 31), range.GetProperty("start"));
        AssertCivilDateTime(new DateOnly(2026, 9, 1), range.GetProperty("end"));
        Assert.Equal(1, document.RootElement.GetProperty("windowSizeDays").GetInt32());
    }

    /// <summary>
    /// A CivilDateTime nests the calendar date under "date" and omits "time" to mean midnight.
    /// Inline year/month/day is the shape the API rejects outright, so it is asserted against.
    /// </summary>
    private static void AssertCivilDateTime(DateOnly expected, JsonElement civilDateTime)
    {
        Assert.False(civilDateTime.TryGetProperty("year", out _));

        var date = civilDateTime.GetProperty("date");
        Assert.Equal(expected.Year, date.GetProperty("year").GetInt32());
        Assert.Equal(expected.Month, date.GetProperty("month").GetInt32());
        Assert.Equal(expected.Day, date.GetProperty("day").GetInt32());
        Assert.False(civilDateTime.TryGetProperty("time", out _));
    }

    [Fact]
    public async Task GetActivitiesAsync_ThrowsGoogleHealthApiException_OnNon2xxResponse()
    {
        var handler = new RoutedFakeHttpHandler()
            .Map("/dataTypes/steps/",
                """{ "error": { "status": "UNAUTHENTICATED" } }""", HttpStatusCode.Unauthorized);

        var (sut, _) = CreateSut(handler);

        await Assert.ThrowsAsync<GoogleHealthApiException>(() => sut.GetActivitiesAsync("bad_token", Today));
    }

    // ── Heart Rate ───────────────────────────────────────────────────────────────

    private const string RestingHeartRateJson = """
        {
          "dataPoints": [
            {
              "dailyRestingHeartRate": {
                "date": { "year": 2026, "month": 8, "day": 5 },
                "beatsPerMinute": "63"
              }
            }
          ]
        }
        """;

    [Fact]
    public async Task GetHeartRateAsync_ReturnsMinMaxAvg_FromDailyRollup()
    {
        var handler = new RoutedFakeHttpHandler()
            .Map("/dataTypes/heart-rate/", Rollup("heartRate",
                """{ "beatsPerMinuteMin": 52, "beatsPerMinuteMax": 141, "beatsPerMinuteAvg": 71 }"""))
            .Map("/dataTypes/daily-resting-heart-rate/", RestingHeartRateJson);

        var (sut, _) = CreateSut(handler);
        var result = await sut.GetHeartRateAsync("token", Today);

        Assert.Equal(52, result.MinHeartRate);
        Assert.Equal(141, result.MaxHeartRate);
        Assert.Equal(71, result.AvgHeartRate);
        Assert.Equal(63, result.RestingHeartRate);
    }

    /// <summary>
    /// daily-resting-heart-rate is a **Daily** record: it supports list and reconcile only, and
    /// answers a rollup with a 400 naming the type. Rolling it up failed every wearable sync in
    /// dev, so the method and the filter field are pinned here rather than left to the shape-only
    /// tests, which route on path and never see either.
    /// <para>
    /// The filter member path is snake_case — the documented pattern is
    /// `{daily_summary_data_type}.date`. Spelling it as the camelCase union member the *response*
    /// uses (`dailyRestingHeartRate.date`) is rejected with INVALID_DATA_POINT_FILTER, which is
    /// what failed every sync next.
    /// </para>
    /// </summary>
    [Fact]
    public async Task GetHeartRateAsync_ListsRestingHeartRate_RatherThanRollingItUp()
    {
        var handler = new RoutedFakeHttpHandler()
            .Map("/dataTypes/daily-resting-heart-rate/", RestingHeartRateJson);

        var (sut, _) = CreateSut(handler);
        // Month end, so the exclusive upper bound also exercises the rollover into the next month.
        await sut.GetHeartRateAsync("token", new DateOnly(2026, 8, 31));

        var request = handler.Requests.Single(r =>
            r.RequestUri!.AbsolutePath.Contains("/dataTypes/daily-resting-heart-rate/"));
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.EndsWith("/dataTypes/daily-resting-heart-rate/dataPoints", request.RequestUri!.AbsolutePath);

        Assert.StartsWith("?filter=", request.RequestUri.Query, StringComparison.Ordinal);
        var filter = Uri.UnescapeDataString(request.RequestUri.Query["?filter=".Length..]);
        Assert.Equal(
            """
            daily_resting_heart_rate.date >= "2026-08-31" AND daily_resting_heart_rate.date < "2026-09-01"
            """,
            filter);
    }

    [Fact]
    public async Task GetHeartRateAsync_ToleratesMissingRestingHeartRateDataType()
    {
        var handler = new RoutedFakeHttpHandler()
            .Map("/dataTypes/heart-rate/", Rollup("heartRate",
                """{ "beatsPerMinuteMin": 52, "beatsPerMinuteMax": 141, "beatsPerMinuteAvg": 71 }"""))
            .Map("/dataTypes/daily-resting-heart-rate/",
                """{ "error": { "status": "NOT_FOUND" } }""", HttpStatusCode.NotFound);

        var (sut, _) = CreateSut(handler);
        var result = await sut.GetHeartRateAsync("token", Today);

        Assert.Null(result.RestingHeartRate);
        Assert.Equal(71, result.AvgHeartRate);
    }

    /// <summary>
    /// A wearer whose device never derives a resting HR gets an empty list, not an error — that is
    /// a fact about the device and must not fail the day's whole snapshot.
    /// </summary>
    [Fact]
    public async Task GetHeartRateAsync_ReturnsNullRestingHeartRate_WhenNoDataPoints()
    {
        var handler = new RoutedFakeHttpHandler()
            .Map("/dataTypes/heart-rate/", Rollup("heartRate", """{ "beatsPerMinuteAvg": 71 }"""))
            .Map("/dataTypes/daily-resting-heart-rate/", """{ "dataPoints": [] }""");

        var (sut, _) = CreateSut(handler);
        var result = await sut.GetHeartRateAsync("token", Today);

        Assert.Null(result.RestingHeartRate);
        Assert.Equal(71, result.AvgHeartRate);
    }

    [Fact]
    public async Task GetHeartRateAsync_ToleratesRestingHeartRate400_WithoutFieldViolations()
    {
        var handler = new RoutedFakeHttpHandler()
            .Map("/dataTypes/heart-rate/", Rollup("heartRate", """{ "beatsPerMinuteAvg": 71 }"""))
            .Map("/dataTypes/daily-resting-heart-rate/",
                """
                {
                  "error": {
                    "code": 400,
                    "status": "INVALID_ARGUMENT",
                    "message": "Data type not available for this user."
                  }
                }
                """,
                HttpStatusCode.BadRequest);

        var (sut, _) = CreateSut(handler);
        var result = await sut.GetHeartRateAsync("token", Today);

        Assert.Null(result.RestingHeartRate);
        Assert.Equal(71, result.AvgHeartRate);
    }

    [Fact]
    public async Task GetHeartRateAsync_Throws_WhenRestingHeartRateRequestIsMalformed()
    {
        // A payload the API could not bind is a bug on this side; swallowing it would report a
        // missing resting HR and silently skew the baseline it anchors. This is the exact body the
        // live API returned for the unsupported `resting-heart-rate` type.
        var handler = new RoutedFakeHttpHandler()
            .Map("/dataTypes/heart-rate/", Rollup("heartRate", """{ "beatsPerMinuteAvg": 71 }"""))
            .Map("/dataTypes/daily-resting-heart-rate/", """
                {
                  "error": {
                    "code": 400,
                    "status": "INVALID_ARGUMENT",
                    "message": "Invalid data type ID referenced in the parent data type collection: resting-heart-rate",
                    "details": [
                      {
                        "@type": "type.googleapis.com/google.rpc.ErrorInfo",
                        "reason": "INVALID_PARENT_DATA_TYPE_COLLECTION"
                      },
                      {
                        "@type": "type.googleapis.com/google.rpc.BadRequest",
                        "fieldViolations": [
                          {
                            "field": "parent",
                            "description": "The data type ID 'resting-heart-rate' is not supported."
                          }
                        ]
                      }
                    ]
                  }
                }
                """, HttpStatusCode.BadRequest);

        var (sut, _) = CreateSut(handler);

        var ex = await Assert.ThrowsAsync<GoogleHealthApiException>(() => sut.GetHeartRateAsync("token", Today));
        Assert.True(ex.IsMalformedRequest);
    }

    [Fact]
    public async Task GetHeartRateAsync_ThrowsGoogleHealthApiException_OnNon2xxResponse()
    {
        var handler = new RoutedFakeHttpHandler()
            .Map("/dataTypes/heart-rate/", "{}", HttpStatusCode.InternalServerError);

        var (sut, _) = CreateSut(handler);

        await Assert.ThrowsAsync<GoogleHealthApiException>(() => sut.GetHeartRateAsync("token", Today));
    }

    // ── Sleep ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A staged session. `summary.stagesSummary` is a list keyed by stage type, not an object with
    /// a field per stage, and its minutes are int64s — so they arrive quoted.
    /// </summary>
    private const string SleepSessionJson = """
        {
          "dataPoints": [
            {
              "sleep": {
                "interval": {
                  "startTime": "2026-08-04T22:30:00Z",
                  "endTime":   "2026-08-05T06:30:00Z"
                },
                "summary": {
                  "stagesSummary": [
                    { "type": "DEEP",  "minutes": "85"  },
                    { "type": "LIGHT", "minutes": "220" },
                    { "type": "REM",   "minutes": "90"  },
                    { "type": "AWAKE", "minutes": "25"  }
                  ],
                  "minutesInSleepPeriod": "435",
                  "minutesAsleep": "395",
                  "minutesAwake": "25"
                }
              }
            }
          ]
        }
        """;

    /// <summary>
    /// The filter grammar pairs each field with one literal format: `civil_end_time` takes an ISO
    /// date, its physical-instant sibling `end_time` demands RFC-3339. Sending the date against
    /// `end_time` is the bug this pins — a 400 carrying reason `INVALID_DATA_POINT_FILTER` with
    /// `detailedReasons: INVALID_DATA_POINT_FILTER_TIMESTAMP_FORMAT` (both strings appear, at the
    /// two levels of one error). The shape-only sleep tests above cannot see it, because the fake
    /// handler routes on path and ignores the query.
    /// </summary>
    [Fact]
    public async Task GetSleepAsync_ListsWithClosedOpenCivilEndTimeFilter()
    {
        var handler = new RoutedFakeHttpHandler()
            .Map("/dataTypes/sleep/", SleepSessionJson);

        var (sut, _) = CreateSut(handler);
        // Month end, so the exclusive upper bound also exercises the rollover into the next month.
        await sut.GetSleepAsync("token", new DateOnly(2026, 8, 31));

        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.EndsWith("/dataTypes/sleep/dataPoints", request.RequestUri!.AbsolutePath);

        Assert.StartsWith("?filter=", request.RequestUri.Query, StringComparison.Ordinal);
        var filter = Uri.UnescapeDataString(request.RequestUri.Query["?filter=".Length..]);
        Assert.Equal(
            """
            sleep.interval.civil_end_time >= "2026-08-31" AND sleep.interval.civil_end_time < "2026-09-01"
            """,
            filter);
    }

    [Fact]
    public async Task GetSleepAsync_ReturnsMinutesAsleep_FromSummary()
    {
        var handler = new RoutedFakeHttpHandler()
            .Map("/dataTypes/sleep/", SleepSessionJson);

        var (sut, _) = CreateSut(handler);
        var result = await sut.GetSleepAsync("token", Today);

        Assert.Equal(395, result.TotalSleepMinutes);
    }

    /// <summary>
    /// A device that does not stage sleep reports one ASLEEP stage and no summary total, so the
    /// three named stages stay null and ASLEEP is the only evidence of time asleep. RESTLESS is
    /// excluded on purpose — Fitbit does not count it as asleep either.
    /// </summary>
    [Fact]
    public async Task GetSleepAsync_SumsStageMinutes_WhenSummaryTotalAbsent()
    {
        var handler = new RoutedFakeHttpHandler()
            .Map("/dataTypes/sleep/", """
                {
                  "dataPoints": [
                    {
                      "sleep": {
                        "interval": {
                          "startTime": "2026-08-04T22:30:00Z",
                          "endTime":   "2026-08-05T06:30:00Z"
                        },
                        "summary": {
                          "stagesSummary": [
                            { "type": "ASLEEP",   "minutes": "410" },
                            { "type": "RESTLESS", "minutes": "20"  },
                            { "type": "AWAKE",    "minutes": "18"  }
                          ]
                        }
                      }
                    }
                  ]
                }
                """);

        var (sut, _) = CreateSut(handler);
        var result = await sut.GetSleepAsync("token", Today);

        Assert.Equal(410, result.TotalSleepMinutes);
        Assert.Equal(18, result.AwakeMinutes);
        Assert.Null(result.DeepSleepMinutes);
        // No minutesInSleepPeriod to divide by, so efficiency stays unknown rather than invented.
        Assert.Null(result.SleepEfficiency);
    }

    [Fact]
    public async Task GetSleepAsync_ReturnsSleepStages_FromSession()
    {
        var handler = new RoutedFakeHttpHandler()
            .Map("/dataTypes/sleep/", SleepSessionJson);

        var (sut, _) = CreateSut(handler);
        var result = await sut.GetSleepAsync("token", Today);

        Assert.Equal(85, result.DeepSleepMinutes);
        Assert.Equal(220, result.LightSleepMinutes);
        Assert.Equal(90, result.RemSleepMinutes);
        Assert.Equal(25, result.AwakeMinutes);
        Assert.NotNull(result.SleepStartTime);
        Assert.NotNull(result.SleepEndTime);
    }

    /// <summary>
    /// Sleep bounds must come back as <c>Kind=Utc</c>. They are the only DateTimes the sync writes,
    /// and their column is `timestamp with time zone`, which Npgsql writes only from UTC — a
    /// <c>Kind=Local</c> value threw <c>ArgumentException</c> inside SaveChanges and took down the
    /// whole day's upsert, sleep fields and step counts alike.
    ///
    /// Kind is asserted, not just the instant: a bare parse of a `Z` literal on a UTC-configured
    /// host yields the right wall-clock reading stamped <c>Local</c>, so value-only assertions stay
    /// green while production fails. The offset literal covers the conversion itself — 23:30-07:00
    /// is 06:30Z the next day.
    /// </summary>
    [Theory]
    [InlineData("2026-08-04T22:30:00Z", "2026-08-05T06:30:00Z", 4, 22, 30, 5, 6, 30)]
    [InlineData("2026-08-04T23:30:00-07:00", "2026-08-05T07:30:00-07:00", 5, 6, 30, 5, 14, 30)]
    // Offsetless: read as UTC rather than as host-local, which would land Kind=Unspecified and be
    // rejected by the same column.
    [InlineData("2026-08-04T22:30:00", "2026-08-05T06:30:00", 4, 22, 30, 5, 6, 30)]
    public async Task GetSleepAsync_ReturnsBoundsAsUtc(
        string start, string end,
        int startDay, int startHour, int startMinute,
        int endDay, int endHour, int endMinute)
    {
        var handler = new RoutedFakeHttpHandler()
            .Map("/dataTypes/sleep/", $$"""
                {
                  "dataPoints": [
                    {
                      "sleep": {
                        "interval": { "startTime": "{{start}}", "endTime": "{{end}}" },
                        "summary": { "minutesAsleep": "395" }
                      }
                    }
                  ]
                }
                """);

        var (sut, _) = CreateSut(handler);
        var result = await sut.GetSleepAsync("token", Today);

        Assert.Equal(new DateTime(2026, 8, startDay, startHour, startMinute, 0, DateTimeKind.Utc),
            result.SleepStartTime);
        Assert.Equal(new DateTime(2026, 8, endDay, endHour, endMinute, 0, DateTimeKind.Utc),
            result.SleepEndTime);
        Assert.Equal(DateTimeKind.Utc, result.SleepStartTime!.Value.Kind);
        Assert.Equal(DateTimeKind.Utc, result.SleepEndTime!.Value.Kind);
    }

    /// <summary>
    /// The API exposes no efficiency field, so it is derived as minutes asleep over minutes in the
    /// sleep period — 395/435 here. Reading a literal `efficiency` member (there isn't one) left
    /// the 1-5 quality bucket permanently empty.
    /// </summary>
    [Fact]
    public async Task GetSleepAsync_DerivesEfficiency_FromAsleepOverSleepPeriod()
    {
        var handler = new RoutedFakeHttpHandler()
            .Map("/dataTypes/sleep/", SleepSessionJson);

        var (sut, _) = CreateSut(handler);
        var result = await sut.GetSleepAsync("token", Today);

        Assert.Equal(91, result.SleepEfficiency);
    }

    /// <summary>
    /// No session for the day is null, not 0: "the wearer logged no sleep session" and "the wearer
    /// slept zero minutes" are different claims, and only the second is a measurement. A 0 would
    /// enter the sleep baseline as a genuine sleepless night every time the watch was off the wrist
    /// or had not yet synced.
    /// </summary>
    [Fact]
    public async Task GetSleepAsync_ReturnsNullTotal_WhenNoSessions()
    {
        var handler = new RoutedFakeHttpHandler()
            .Map("/dataTypes/sleep/", """{ "dataPoints": [] }""");

        var (sut, _) = CreateSut(handler);
        var result = await sut.GetSleepAsync("token", Today);

        Assert.Null(result.TotalSleepMinutes);
        Assert.Null(result.SleepEfficiency);
        Assert.Null(result.SleepStartTime);
    }

    [Fact]
    public async Task GetSleepAsync_ThrowsGoogleHealthApiException_OnNon2xxResponse()
    {
        var handler = new RoutedFakeHttpHandler()
            .Map("/dataTypes/sleep/", "{}", HttpStatusCode.Unauthorized);

        var (sut, _) = CreateSut(handler);

        await Assert.ThrowsAsync<GoogleHealthApiException>(() => sut.GetSleepAsync("bad_token", Today));
    }

    // ── Additional metrics ───────────────────────────────────────────────────────

    private static string SpO2Samples(string? nextPageToken, params string[] percentages)
    {
        var points = string.Join(",\n", percentages.Select(p => $$"""
            { "oxygenSaturation": { "percentage": {{p}} } }
            """));
        var token = nextPageToken is null ? "" : $$""", "nextPageToken": "{{nextPageToken}}" """;
        return $$"""{ "dataPoints": [ {{points}} ]{{token}} }""";
    }

    private static string DailyRecord(string unionMember, string valueJson) =>
        $$"""{ "dataPoints": [ { "{{unionMember}}": {{valueJson}} } ] }""";

    /// <summary>
    /// The decoded `filter` query parameter, whichever position it occupies — the sample reads pair
    /// it with a `pageSize`, so it is not always the only one.
    /// </summary>
    private static string FilterOf(HttpRequestMessage request)
    {
        var pair = request.RequestUri!.Query.TrimStart('?')
            .Split('&')
            .Single(p => p.StartsWith("filter=", StringComparison.Ordinal));
        return Uri.UnescapeDataString(pair["filter=".Length..]);
    }

    /// <summary>
    /// All three SpO2 figures come from the sample series, so they describe one series and agree
    /// with each other. The average is over the readings themselves — 95.0 here, not the midpoint
    /// of the range.
    /// </summary>
    [Fact]
    public async Task GetAdditionalMetricsAsync_DerivesSpO2AverageMinAndMax_FromSampleSeries()
    {
        var handler = new RoutedFakeHttpHandler()
            .Map("/dataTypes/oxygen-saturation/", SpO2Samples(null, "96.5", "92.0", "97.4", "94.1"));

        var (sut, _) = CreateSut(handler);
        var result = await sut.GetAdditionalMetricsAsync("token", Today);

        Assert.Equal(95.0m, result.SpO2Average);
        Assert.Equal(92.0m, result.SpO2Min);
        Assert.Equal(97.4m, result.SpO2Max);
    }

    /// <summary>
    /// A Sample type is filtered on `{data_type}.sample_time.civil_time` — a different pattern from
    /// both the Daily `{type}.date` and the interval `{type}.interval.civil_start_time`, and
    /// snake_case like all of them. Civil rather than physical time keeps a night's readings inside
    /// the wearer's own day instead of splitting them across two UTC days.
    /// </summary>
    [Fact]
    public async Task GetAdditionalMetricsAsync_ListsOxygenSaturationOnCivilSampleTime()
    {
        var handler = new RoutedFakeHttpHandler()
            .Map("/dataTypes/oxygen-saturation/", SpO2Samples(null, "95.0"));

        var (sut, _) = CreateSut(handler);
        // Month end, so the exclusive upper bound also exercises the rollover into the next month.
        await sut.GetAdditionalMetricsAsync("token", new DateOnly(2026, 8, 31));

        var request = handler.Requests.Single(r =>
            r.RequestUri!.AbsolutePath.Contains("/dataTypes/oxygen-saturation/"));
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.EndsWith("/dataTypes/oxygen-saturation/dataPoints", request.RequestUri!.AbsolutePath);

        Assert.Equal(
            """
            oxygen_saturation.sample_time.civil_time >= "2026-08-31" AND oxygen_saturation.sample_time.civil_time < "2026-09-01"
            """,
            FilterOf(request));
    }

    /// <summary>
    /// The tail of a paged series must be read. Dropping page two would understate the maximum and
    /// overstate the minimum — silently, and in the direction that hides a desaturation.
    /// </summary>
    [Fact]
    public async Task GetAdditionalMetricsAsync_FollowsSpO2Pagination()
    {
        var handler = new RoutedFakeHttpHandler()
            .MapSequence(
                "/dataTypes/oxygen-saturation/",
                SpO2Samples("page-2", "96.0", "95.0"),
                SpO2Samples(null, "88.5", "97.0"));

        var (sut, _) = CreateSut(handler);
        var result = await sut.GetAdditionalMetricsAsync("token", Today);

        Assert.Equal(88.5m, result.SpO2Min);
        Assert.Equal(97.0m, result.SpO2Max);
        Assert.Equal(2, handler.Requests.Count(r =>
            r.RequestUri!.AbsolutePath.Contains("/dataTypes/oxygen-saturation/")));
    }

    /// <summary>
    /// Past the cap with pages still outstanding, the read fails rather than returning the prefix it
    /// has. Average, min and max computed over part of a longer window are not the day's figures,
    /// and reporting them as such would be wrong silently and indefinitely — the cap is only
    /// reachable when the request is selecting more than the civil day it asked for.
    /// </summary>
    [Fact]
    public async Task GetAdditionalMetricsAsync_Throws_WhenSampleSeriesExceedsTheDailyCap()
    {
        var overCap = Enumerable.Repeat("95.0", 100_000).ToArray();
        var handler = new RoutedFakeHttpHandler()
            .MapSequence("/dataTypes/oxygen-saturation/", SpO2Samples("page-2", overCap));

        var (sut, _) = CreateSut(handler);

        var ex = await Assert.ThrowsAsync<GoogleHealthApiException>(
            () => sut.GetAdditionalMetricsAsync("token", Today));
        Assert.Contains("selecting more than one day", ex.Message, StringComparison.Ordinal);

        // Not tolerated as "this device has no SpO2": that guard keys on 400/404, and swallowing
        // this would put the silent wrong answer back.
        Assert.Equal(0, ex.StatusCode);
    }

    /// <summary>
    /// A device that publishes only the daily summary still contributes its average. Min and max
    /// stay null: the summary's lowerBound/upperBound pair describes the spread of the day's
    /// distribution, not the lowest and highest readings taken, and putting a distribution bound in
    /// SpO2Min would misreport exactly the figure a desaturation check reads.
    /// </summary>
    [Fact]
    public async Task GetAdditionalMetricsAsync_FallsBackToDailySpO2Average_WhenSeriesIsEmpty()
    {
        var handler = new RoutedFakeHttpHandler()
            .Map("/dataTypes/oxygen-saturation/", """{ "dataPoints": [] }""")
            .Map("/dataTypes/daily-oxygen-saturation/", DailyRecord("dailyOxygenSaturation", """
                {
                  "averagePercentage": 94.28,
                  "lowerBoundPercentage": 90.0,
                  "upperBoundPercentage": 98.0,
                  "standardDeviationPercentage": 1.5
                }
                """));

        var (sut, _) = CreateSut(handler);
        var result = await sut.GetAdditionalMetricsAsync("token", Today);

        Assert.Equal(94.3m, result.SpO2Average);
        Assert.Null(result.SpO2Min);
        Assert.Null(result.SpO2Max);
    }

    [Fact]
    public async Task GetAdditionalMetricsAsync_ReadsVo2MaxBreathingRateAndTemperature_FromDailyRecords()
    {
        var handler = new RoutedFakeHttpHandler()
            .Map("/dataTypes/daily-vo2-max/", DailyRecord("dailyVo2Max", """
                { "vo2Max": 34.7, "estimated": true, "cardioFitnessLevel": "AVERAGE" }
                """))
            .Map("/dataTypes/daily-respiratory-rate/",
                DailyRecord("dailyRespiratoryRate", """{ "breathsPerMinute": 15.4 }"""))
            .Map("/dataTypes/daily-sleep-temperature-derivations/",
                DailyRecord("dailySleepTemperatureDerivations", """
                {
                  "nightlyTemperatureCelsius": 33.8,
                  "baselineTemperatureCelsius": 33.2,
                  "relativeNightlyStddev30dCelsius": 0.4
                }
                """));

        var (sut, _) = CreateSut(handler);
        var result = await sut.GetAdditionalMetricsAsync("token", Today);

        Assert.Equal(34.7m, result.VO2Max);
        Assert.Equal(15.4m, result.BreathingRate);
        Assert.Equal(33.8m, result.Temperature);
        Assert.Equal(33.2m, result.TemperatureBaseline);
        Assert.Equal(0.4m, result.TemperatureVariation);
    }

    /// <summary>
    /// Each Daily record is filtered on the snake_case data type, not the camelCase union member the
    /// response is keyed by — the mistake that made every resting-HR read a 400.
    /// </summary>
    [Theory]
    [InlineData("daily-vo2-max", "daily_vo2_max")]
    [InlineData("daily-respiratory-rate", "daily_respiratory_rate")]
    [InlineData("daily-sleep-temperature-derivations", "daily_sleep_temperature_derivations")]
    public async Task GetAdditionalMetricsAsync_FiltersDailyRecordsOnSnakeCaseDate(
        string dataType, string filterMember)
    {
        var (sut, handler) = CreateSut();

        await sut.GetAdditionalMetricsAsync("token", new DateOnly(2026, 8, 31));

        var request = handler.Requests.Single(r =>
            r.RequestUri!.AbsolutePath.Contains($"/dataTypes/{dataType}/"));
        Assert.Equal(HttpMethod.Get, request.Method);

        Assert.Equal(
            $"{filterMember}.date >= \"2026-08-31\" AND {filterMember}.date < \"2026-09-01\"",
            FilterOf(request));
    }

    /// <summary>
    /// Most Fitbits derive none of these. An absent data type is a fact about the device, so it
    /// yields null rather than failing the day's whole snapshot — 404 and a 400 without field
    /// violations alike.
    /// </summary>
    [Fact]
    public async Task GetAdditionalMetricsAsync_ToleratesAbsentDataTypes()
    {
        var handler = new RoutedFakeHttpHandler()
            .Map("/dataTypes/oxygen-saturation/", """{ "error": { "status": "NOT_FOUND" } }""",
                HttpStatusCode.NotFound)
            .Map("/dataTypes/daily-vo2-max/", """
                { "error": { "code": 400, "message": "Data type not available for this user." } }
                """, HttpStatusCode.BadRequest)
            .Map("/dataTypes/daily-respiratory-rate/", """{ "error": { "status": "NOT_FOUND" } }""",
                HttpStatusCode.NotFound)
            .Map("/dataTypes/daily-sleep-temperature-derivations/",
                """{ "error": { "status": "NOT_FOUND" } }""", HttpStatusCode.NotFound);

        var (sut, _) = CreateSut(handler);
        var result = await sut.GetAdditionalMetricsAsync("token", Today);

        Assert.Null(result.SpO2Average);
        Assert.Null(result.SpO2Min);
        Assert.Null(result.SpO2Max);
        Assert.Null(result.VO2Max);
        Assert.Null(result.BreathingRate);
        Assert.Null(result.Temperature);
        Assert.Null(result.TemperatureBaseline);
        Assert.Null(result.TemperatureVariation);
    }

    /// <summary>
    /// A 400 carrying field violations is a request this client built wrong. Tolerating it would
    /// leave the column permanently empty and looking merely unsupported, so it throws even though
    /// the metric itself is optional.
    /// </summary>
    [Fact]
    public async Task GetAdditionalMetricsAsync_Throws_WhenRequestIsMalformed()
    {
        var handler = new RoutedFakeHttpHandler()
            .Map("/dataTypes/daily-vo2-max/", """
                {
                  "error": {
                    "code": 400,
                    "status": "INVALID_ARGUMENT",
                    "details": [
                      {
                        "@type": "type.googleapis.com/google.rpc.BadRequest",
                        "fieldViolations": [ { "field": "filter", "description": "Unknown field." } ]
                      }
                    ]
                  }
                }
                """, HttpStatusCode.BadRequest);

        var (sut, _) = CreateSut(handler);

        var ex = await Assert.ThrowsAsync<GoogleHealthApiException>(
            () => sut.GetAdditionalMetricsAsync("token", Today));
        Assert.True(ex.IsMalformedRequest);
    }

    // ── Snapshot ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// The snapshot is the contract the sync writes from, so the additional metrics have to survive
    /// the trip through it. StressScore is asserted null on purpose: Google Health API v4 has no
    /// stress or readiness data type, so nothing may quietly appear in that column.
    /// </summary>
    [Fact]
    public async Task GetHealthSnapshotAsync_CarriesAdditionalMetrics_AndLeavesStressScoreNull()
    {
        var handler = new RoutedFakeHttpHandler()
            .Map("/dataTypes/oxygen-saturation/", SpO2Samples(null, "96.0", "93.0"))
            .Map("/dataTypes/daily-vo2-max/", DailyRecord("dailyVo2Max", """{ "vo2Max": 34.7 }"""))
            .Map("/dataTypes/daily-respiratory-rate/",
                DailyRecord("dailyRespiratoryRate", """{ "breathsPerMinute": 15.4 }"""))
            .Map("/dataTypes/daily-sleep-temperature-derivations/",
                DailyRecord("dailySleepTemperatureDerivations", """
                {
                  "nightlyTemperatureCelsius": 33.8,
                  "baselineTemperatureCelsius": 33.2,
                  "relativeNightlyStddev30dCelsius": 0.4
                }
                """))
            .Map("/dataTypes/sedentary-period/", Rollup("sedentaryPeriod", """{ "durationSum": "28800s" }"""));

        var (sut, _) = CreateSut(handler);
        var snapshot = await ((IDeviceApiClient)sut).GetHealthSnapshotAsync("token", Today);

        Assert.Equal(94.5m, snapshot.SpO2Average);
        Assert.Equal(93.0m, snapshot.SpO2Min);
        Assert.Equal(96.0m, snapshot.SpO2Max);
        Assert.Equal(34.7m, snapshot.VO2Max);
        Assert.Equal(15.4m, snapshot.BreathingRate);
        Assert.Equal(33.8m, snapshot.Temperature);
        Assert.Equal(33.2m, snapshot.TemperatureBaseline);
        Assert.Equal(0.4m, snapshot.TemperatureVariation);
        Assert.Equal(480, snapshot.SedentaryMinutes);
        Assert.Null(snapshot.StressScore);
    }

    // ── Granular day ─────────────────────────────────────────────────────────────
    //
    // The sub-daily series feeding GranularMetricHours. Field names and record shapes below are
    // the v4 discovery document's, same as everywhere else in this file: a sample carries its
    // instant under sampleTime.physicalTime, an interval under interval.startTime, and every
    // int64 value crosses the wire as a JSON string.

    private static string ListPoints(string unionMember, params string[] valueJson)
    {
        var points = string.Join(",", valueJson.Select(v => $$"""{ "{{unionMember}}": {{v}} }"""));
        return $$"""{ "dataPoints": [ {{points}} ] }""";
    }

    private static IDeviceApiClient GranularSut(RoutedFakeHttpHandler? handler = null) =>
        (IDeviceApiClient)CreateSut(handler).Sut;

    [Fact]
    public async Task GetGranularDayAsync_ParsesHeartRateSamples_TimeAndInt64Value()
    {
        var handler = new RoutedFakeHttpHandler()
            .Map("/dataTypes/heart-rate/", ListPoints("heartRate",
                """{ "sampleTime": { "physicalTime": "2026-08-05T10:05:00Z" }, "beatsPerMinute": "72" }""",
                """{ "sampleTime": { "physicalTime": "2026-08-05T10:06:00Z" }, "beatsPerMinute": "74" }"""));

        var day = await GranularSut(handler).GetGranularDayAsync("token", new DateOnly(2026, 8, 5));

        Assert.Equal(2, day.HeartRate.Count);
        Assert.Equal(new DateTime(2026, 8, 5, 10, 5, 0, DateTimeKind.Utc), day.HeartRate[0].TimeUtc);
        Assert.Equal(72f, day.HeartRate[0].Value);
        Assert.Equal(74f, day.HeartRate[1].Value);
        Assert.True(day.HasAnyData);
    }

    [Fact]
    public async Task GetGranularDayAsync_StampsIntervalTypesAtTheirStart()
    {
        var handler = new RoutedFakeHttpHandler()
            .Map("/dataTypes/steps/", ListPoints("steps",
                """
                {
                  "interval": { "startTime": "2026-08-05T09:00:00Z", "endTime": "2026-08-05T09:01:00Z" },
                  "count": "83"
                }
                """));

        var day = await GranularSut(handler).GetGranularDayAsync("token", new DateOnly(2026, 8, 5));

        var sample = Assert.Single(day.Steps);
        Assert.Equal(new DateTime(2026, 8, 5, 9, 0, 0, DateTimeKind.Utc), sample.TimeUtc);
        Assert.Equal(83f, sample.Value);
    }

    // AZM arrives once per heart-rate zone, so one instant can legitimately appear twice; both
    // entries are kept because they are additive quantities the bucketing sums.
    [Fact]
    public async Task GetGranularDayAsync_KeepsOnePointPerZone_ForActiveZoneMinutes()
    {
        var handler = new RoutedFakeHttpHandler()
            .Map("/dataTypes/active-zone-minutes/", ListPoints("activeZoneMinutes",
                """
                {
                  "interval": { "startTime": "2026-08-05T17:30:00Z", "endTime": "2026-08-05T17:31:00Z" },
                  "heartRateZone": "FAT_BURN",
                  "activeZoneMinutes": "1"
                }
                """,
                """
                {
                  "interval": { "startTime": "2026-08-05T17:30:00Z", "endTime": "2026-08-05T17:31:00Z" },
                  "heartRateZone": "CARDIO",
                  "activeZoneMinutes": "2"
                }
                """));

        var day = await GranularSut(handler).GetGranularDayAsync("token", new DateOnly(2026, 8, 5));

        Assert.Equal(2, day.ActiveZoneMinutes.Count);
        Assert.Equal(day.ActiveZoneMinutes[0].TimeUtc, day.ActiveZoneMinutes[1].TimeUtc);
        Assert.Equal(3f, day.ActiveZoneMinutes.Sum(s => s.Value));
    }

    [Fact]
    public async Task GetGranularDayAsync_ParsesSpO2Percentage()
    {
        var handler = new RoutedFakeHttpHandler()
            .Map("/dataTypes/oxygen-saturation/", ListPoints("oxygenSaturation",
                """{ "sampleTime": { "physicalTime": "2026-08-05T03:10:00Z" }, "percentage": 95.5 }"""));

        var day = await GranularSut(handler).GetGranularDayAsync("token", new DateOnly(2026, 8, 5));

        var sample = Assert.Single(day.SpO2);
        Assert.Equal(95.5f, sample.Value);
    }

    /// <summary>
    /// The filter grammar is the one place the API's naming convention flips to snake_case, and
    /// sample and interval types filter on different member paths — pinned per type, on a month
    /// boundary so the exclusive bound's rollover is exercised too.
    /// </summary>
    [Theory]
    [InlineData("heart-rate", """heart_rate.sample_time.civil_time >= "2026-08-31" AND heart_rate.sample_time.civil_time < "2026-09-01" """)]
    [InlineData("steps", """steps.interval.civil_start_time >= "2026-08-31" AND steps.interval.civil_start_time < "2026-09-01" """)]
    [InlineData("active-zone-minutes", """active_zone_minutes.interval.civil_start_time >= "2026-08-31" AND active_zone_minutes.interval.civil_start_time < "2026-09-01" """)]
    [InlineData("oxygen-saturation", """oxygen_saturation.sample_time.civil_time >= "2026-08-31" AND oxygen_saturation.sample_time.civil_time < "2026-09-01" """)]
    public async Task GetGranularDayAsync_PinsTheGranularFilters(string dataType, string expectedFilter)
    {
        var (sut, handler) = CreateSut();

        await ((IDeviceApiClient)sut).GetGranularDayAsync("token", new DateOnly(2026, 8, 31));

        var request = handler.Requests.Single(r =>
            r.RequestUri!.AbsolutePath.Contains($"/dataTypes/{dataType}/"));
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(expectedFilter.TrimEnd(), FilterOf(request));
    }

    [Fact]
    public async Task GetGranularDayAsync_ToleratesAbsentDataTypes()
    {
        var handler = new RoutedFakeHttpHandler()
            .Map("/dataTypes/heart-rate/", """{ "error": { "status": "NOT_FOUND" } }""",
                HttpStatusCode.NotFound)
            .Map("/dataTypes/steps/", """
                { "error": { "code": 400, "message": "Data type not available for this user." } }
                """, HttpStatusCode.BadRequest)
            .Map("/dataTypes/active-zone-minutes/", """{ "error": { "status": "NOT_FOUND" } }""",
                HttpStatusCode.NotFound)
            .Map("/dataTypes/oxygen-saturation/", """{ "error": { "status": "NOT_FOUND" } }""",
                HttpStatusCode.NotFound);

        var day = await GranularSut(handler).GetGranularDayAsync("token", Today);

        Assert.False(day.HasAnyData);
        // The shared instance, as the interface contract promises.
        Assert.Same(DeviceGranularDay.Empty, day);
    }

    // Same rule as everywhere else in this client: a 400 carrying field violations is a bug in a
    // filter built here, and tolerating it would read as a device without sensors forever.
    [Fact]
    public async Task GetGranularDayAsync_Throws_WhenRequestIsMalformed()
    {
        var handler = new RoutedFakeHttpHandler()
            .Map("/dataTypes/heart-rate/", """
                {
                  "error": {
                    "code": 400,
                    "status": "INVALID_ARGUMENT",
                    "details": [
                      {
                        "@type": "type.googleapis.com/google.rpc.BadRequest",
                        "fieldViolations": [ { "field": "filter", "description": "Unknown field." } ]
                      }
                    ]
                  }
                }
                """, HttpStatusCode.BadRequest);

        var ex = await Assert.ThrowsAsync<GoogleHealthApiException>(
            () => GranularSut(handler).GetGranularDayAsync("token", Today));
        Assert.True(ex.IsMalformedRequest);
    }

    // The schema marks both time and value Required, so a point missing either is malformed —
    // and a reading that cannot be placed on the clock cannot be bucketed into a minute grid.
    [Fact]
    public async Task GetGranularDayAsync_SkipsPointsMissingTimeOrValue()
    {
        var handler = new RoutedFakeHttpHandler()
            .Map("/dataTypes/heart-rate/", ListPoints("heartRate",
                """{ "beatsPerMinute": "72" }""",
                """{ "sampleTime": { "physicalTime": "2026-08-05T10:06:00Z" } }""",
                """{ "sampleTime": { "physicalTime": "2026-08-05T10:07:00Z" }, "beatsPerMinute": "75" }"""));

        var day = await GranularSut(handler).GetGranularDayAsync("token", new DateOnly(2026, 8, 5));

        var sample = Assert.Single(day.HeartRate);
        Assert.Equal(75f, sample.Value);
    }

    // ── Identity ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetHealthUserIdAsync_ReadsTheIdentityResource()
    {
        var handler = new RoutedFakeHttpHandler()
            .Map("/users/me/identity",
                """{ "name": "users/me/identity", "healthUserId": "abc-123", "legacyUserId": "XYZ789" }""");

        var id = await ((IDeviceApiClient)CreateSut(handler).Sut).GetHealthUserIdAsync("token");

        Assert.Equal("abc-123", id);
    }

    [Fact]
    public async Task GetHealthUserIdAsync_ToleratesAnAbsentIdentity()
    {
        var handler = new RoutedFakeHttpHandler()
            .Map("/users/me/identity", """{ "error": { "status": "NOT_FOUND" } }""",
                HttpStatusCode.NotFound);

        var id = await ((IDeviceApiClient)CreateSut(handler).Sut).GetHealthUserIdAsync("token");

        Assert.Null(id);
    }

    [Fact]
    public async Task GetHealthUserIdAsync_Throws_WhenTheRequestIsMalformed()
    {
        var handler = new RoutedFakeHttpHandler()
            .Map("/users/me/identity", """
                {
                  "error": {
                    "code": 400,
                    "details": [
                      {
                        "@type": "type.googleapis.com/google.rpc.BadRequest",
                        "fieldViolations": [ { "field": "name" } ]
                      }
                    ]
                  }
                }
                """, HttpStatusCode.BadRequest);

        var ex = await Assert.ThrowsAsync<GoogleHealthApiException>(() =>
            ((IDeviceApiClient)CreateSut(handler).Sut).GetHealthUserIdAsync("token"));
        Assert.True(ex.IsMalformedRequest);
    }

    // ── Paired devices (battery) ─────────────────────────────────────────────────

    [Fact]
    public async Task GetPairedDevicesAsync_ReadsBatteryOffThePairedDeviceResource()
    {
        var handler = new RoutedFakeHttpHandler()
            .Map("/users/me/pairedDevices", """
                {
                  "pairedDevices": [
                    {
                      "name": "users/1234567890/pairedDevices/123",
                      "deviceType": "TRACKER",
                      "batteryLevel": 8,
                      "batteryStatus": "Low",
                      "deviceVersion": "Charge 6",
                      "lastSyncTime": "2026-08-13T09:30:00Z"
                    }
                  ]
                }
                """);

        var devices = await ((IDeviceApiClient)CreateSut(handler).Sut).GetPairedDevicesAsync("token");

        var device = Assert.Single(devices);
        Assert.Equal("TRACKER", device.DeviceType);
        Assert.Equal(8, device.BatteryLevel);
        Assert.Equal("Low", device.BatteryStatus);
        Assert.Equal("Charge 6", device.DeviceVersion);
        Assert.Equal(new DateTime(2026, 8, 13, 9, 30, 0, DateTimeKind.Utc), device.LastSyncTimeUtc);
        Assert.True(device.IsBatteryPowered);
    }

    [Fact]
    public async Task GetPairedDevicesAsync_ReturnsEmpty_WhenTheSettingsScopeWasNeverGranted()
    {
        // The steady state for every connection authorised before the settings scope shipped. A
        // throw here would park a working connection in SyncError over telemetry, every ten
        // minutes, for a wearer whose health data is arriving perfectly well.
        var handler = new RoutedFakeHttpHandler()
            .Map("/users/me/pairedDevices",
                """{ "error": { "code": 403, "status": "PERMISSION_DENIED" } }""",
                HttpStatusCode.Forbidden);

        var devices = await ((IDeviceApiClient)CreateSut(handler).Sut).GetPairedDevicesAsync("token");

        Assert.Empty(devices);
    }

    [Fact]
    public async Task GetPairedDevicesAsync_ToleratesAnAccountWithNoDeviceRegistry()
    {
        var handler = new RoutedFakeHttpHandler()
            .Map("/users/me/pairedDevices", """{ "error": { "status": "NOT_FOUND" } }""",
                HttpStatusCode.NotFound);

        Assert.Empty(await ((IDeviceApiClient)CreateSut(handler).Sut).GetPairedDevicesAsync("token"));
    }

    [Fact]
    public async Task GetPairedDevicesAsync_Throws_WhenTheRequestIsMalformed()
    {
        // The tolerance above is for "not permitted" and "nothing there", never for a bug in the
        // URL built here — that must not present as a device which merely reports no battery.
        var handler = new RoutedFakeHttpHandler()
            .Map("/users/me/pairedDevices", """
                {
                  "error": {
                    "code": 400,
                    "details": [
                      {
                        "@type": "type.googleapis.com/google.rpc.BadRequest",
                        "fieldViolations": [ { "field": "parent" } ]
                      }
                    ]
                  }
                }
                """, HttpStatusCode.BadRequest);

        var ex = await Assert.ThrowsAsync<GoogleHealthApiException>(() =>
            ((IDeviceApiClient)CreateSut(handler).Sut).GetPairedDevicesAsync("token"));
        Assert.True(ex.IsMalformedRequest);
    }

    [Fact]
    public async Task GetPairedDevicesAsync_TreatsAScaleAsCarryingNoBattery()
    {
        var handler = new RoutedFakeHttpHandler()
            .Map("/users/me/pairedDevices", """
                {
                  "pairedDevices": [
                    { "deviceType": "SCALE", "deviceVersion": "Aria Air" }
                  ]
                }
                """);

        var devices = await ((IDeviceApiClient)CreateSut(handler).Sut).GetPairedDevicesAsync("token");

        Assert.False(Assert.Single(devices).IsBatteryPowered);
    }

    [Fact]
    public async Task GetPairedDevicesAsync_ReturnsEmpty_WhenTheAccountHasNoPairedDevices()
    {
        var handler = new RoutedFakeHttpHandler().Map("/users/me/pairedDevices", "{ }");

        Assert.Empty(await ((IDeviceApiClient)CreateSut(handler).Sut).GetPairedDevicesAsync("token"));
    }

    [Fact]
    public async Task GetGranularDayAsync_FollowsPagination()
    {
        var page1 = $$"""
            {
              "dataPoints": [
                { "heartRate": { "sampleTime": { "physicalTime": "2026-08-05T10:05:00Z" }, "beatsPerMinute": "72" } }
              ],
              "nextPageToken": "page-2"
            }
            """;
        var page2 = ListPoints("heartRate",
            """{ "sampleTime": { "physicalTime": "2026-08-05T10:06:00Z" }, "beatsPerMinute": "74" }""");
        var handler = new RoutedFakeHttpHandler()
            .MapSequence("/dataTypes/heart-rate/", page1, page2);

        var day = await GranularSut(handler).GetGranularDayAsync("token", new DateOnly(2026, 8, 5));

        Assert.Equal(2, day.HeartRate.Count);
        Assert.Equal(74f, day.HeartRate[1].Value);
    }

    /// <summary>
    /// A continuous-tracking wearer's heart rate can legitimately run well past the once-a-minute
    /// cadence the daily cap used to assume (see <see cref="GetAdditionalMetricsAsync_Throws_WhenSampleSeriesExceedsTheDailyCap"/>
    /// for the still-enforced ceiling above it) — two pages here, comfortably past the old 20,000
    /// cap and comfortably under the raised one, must land as real data rather than tripping the
    /// guard meant for a mis-scoped filter. Kept to 24,000 points rather than the raised cap's full
    /// 100,000: enough to prove multi-page continuation past the old ceiling without the extra
    /// allocation a much larger fixture would cost every test run.
    /// </summary>
    [Fact]
    public async Task GetGranularDayAsync_AcceptsHeartRateSeries_WellPastOnceAMinuteCadence()
    {
        var highCadence = Enumerable.Repeat("72", 12_000).ToArray();
        var handler = new RoutedFakeHttpHandler()
            .MapSequence(
                "/dataTypes/heart-rate/",
                SamplePage("heartRate", "beatsPerMinute", "page-2", highCadence),
                SamplePage("heartRate", "beatsPerMinute", null, highCadence));

        var day = await GranularSut(handler).GetGranularDayAsync("token", new DateOnly(2026, 8, 5));

        Assert.Equal(24_000, day.HeartRate.Count);
    }

    /// <summary>
    /// Only the second and later page requests wait — the first fires immediately, since most
    /// series are one page and delaying every read would slow every sync for a limit only
    /// multi-page reads can trip. Asserted on the gap between the two heart-rate requests'
    /// arrival timestamps specifically, not the call's total wall-clock time: the other three
    /// series still read after heart-rate in the same call, and on a loaded test runner their
    /// unrelated overhead could push total elapsed past the pacing threshold even with the delay
    /// logic removed, passing the test for the wrong reason.
    /// </summary>
    [Fact]
    public async Task GetGranularDayAsync_PacesPageRequests_AfterTheFirst()
    {
        var page1 = SamplePage("heartRate", "beatsPerMinute", "page-2", "72");
        var page2 = SamplePage("heartRate", "beatsPerMinute", null, "74");
        var handler = new RoutedFakeHttpHandler()
            .MapSequence("/dataTypes/heart-rate/", page1, page2);

        var pacing = TimeSpan.FromMilliseconds(200);
        var (sut, _) = CreateSut(handler, pacing);

        await ((IDeviceApiClient)sut).GetGranularDayAsync("token", new DateOnly(2026, 8, 5));

        var timestamps = handler.TimestampsFor("/dataTypes/heart-rate/");
        Assert.Equal(2, timestamps.Count);
        var gap = timestamps[1] - timestamps[0];
        Assert.True(gap >= pacing, $"Expected the second page to wait at least {pacing}, gap was {gap}.");
    }

    // ── Heart rate variability ───────────────────────────────────────────────────

    /// <summary>
    /// HRV is a Daily record, so it is listed on its own `date` rather than rolled up — and its
    /// value is a `double`, not one of the quoted int64s the counts around it use.
    /// </summary>
    [Fact]
    public async Task GetAdditionalMetricsAsync_ReadsOvernightRmssd_FromTheDailyRecord()
    {
        var handler = new RoutedFakeHttpHandler()
            .Map("/dataTypes/daily-heart-rate-variability/", """
                {
                  "dataPoints": [
                    {
                      "dailyHeartRateVariability": {
                        "date": { "year": 2026, "month": 8, "day": 5 },
                        "averageHeartRateVariabilityMilliseconds": 27.4
                      }
                    }
                  ]
                }
                """);

        var (sut, _) = CreateSut(handler);
        var result = await sut.GetAdditionalMetricsAsync("token", Today);

        Assert.Equal(27.4m, result.HeartRateVariabilityMs);
    }

    /// <summary>
    /// A great many wearables derive no HRV at all. That is a fact about the device: null, never a
    /// substituted figure, and never a failed snapshot.
    /// </summary>
    [Fact]
    public async Task GetAdditionalMetricsAsync_ReturnsNullRmssd_WhenTheDeviceDerivesNone()
    {
        var handler = new RoutedFakeHttpHandler()
            .Map("/dataTypes/daily-heart-rate-variability/", """{ "dataPoints": [] }""");

        var (sut, _) = CreateSut(handler);
        var result = await sut.GetAdditionalMetricsAsync("token", Today);

        Assert.Null(result.HeartRateVariabilityMs);
    }

    // ── Overnight breathing, effort zones and unbroken rest ──────────────────────

    /// <summary>
    /// The overnight figure comes off `respiratory-rate-sleep-summary`, not the daily respiratory
    /// record this client already reads: one averages hours of stillness, the other a whole day
    /// with stairs in it, and the alert that fires on a rise only means anything for the first.
    /// </summary>
    [Fact]
    public async Task GetAdditionalMetricsAsync_ReadsOvernightBreathing_FromTheSleepSummary()
    {
        var handler = new RoutedFakeHttpHandler()
            .Map("/dataTypes/respiratory-rate-sleep-summary/", """
                {
                  "dataPoints": [
                    {
                      "respiratoryRateSleepSummary": {
                        "sampleTime": { "physicalTime": "2026-08-05T06:30:00Z" },
                        "fullSleepStats": { "breathsPerMinute": 15.4, "standardDeviation": 0.8 },
                        "deepSleepStats": { "breathsPerMinute": 14.9 }
                      }
                    }
                  ]
                }
                """);

        var (sut, _) = CreateSut(handler);
        var result = await sut.GetAdditionalMetricsAsync("token", Today);

        Assert.Equal(15.4m, result.OvernightBreathingRate);
    }

    /// <summary>
    /// Zone durations are protobuf Durations ("1800s"), like sedentary-period — parsing them as
    /// bare numbers returns null on every wearer, which is indistinguishable from a still day.
    /// </summary>
    [Fact]
    public async Task GetExertionAsync_ReadsZoneMinutes_FromDurationStrings()
    {
        var handler = new RoutedFakeHttpHandler()
            .Map("/dataTypes/time-in-heart-rate-zone/", Rollup("timeInHeartRateZone", """
                {
                  "timeInHeartRateZones": [
                    { "heartRateZone": "LIGHT",    "duration": "3600s" },
                    { "heartRateZone": "MODERATE", "duration": "1500s" },
                    { "heartRateZone": "VIGOROUS", "duration": "300s"  }
                  ]
                }
                """));

        var (sut, _) = CreateSut(handler);
        var result = await sut.GetExertionAsync("token", Today);

        Assert.Equal(60, result.LightZoneMinutes);
        Assert.Equal(25, result.ModerateZoneMinutes);
        Assert.Equal(5, result.VigorousZoneMinutes);
        // Present rollup, absent zone: the wearer was measured and never reached peak. A real zero,
        // not a gap — which is what lets the elevated-minutes sum mean something.
        Assert.Equal(0, result.PeakZoneMinutes);
    }

    /// <summary>
    /// The moderate-zone floor is the wearer's own Karvonen figure, read rather than re-derived so
    /// CardiTrack's copy and their watch cannot disagree about where effort starts for them.
    /// </summary>
    [Fact]
    public async Task GetExertionAsync_ReadsTheModerateZoneFloor_FromTheDailyZonesRecord()
    {
        var handler = new RoutedFakeHttpHandler()
            .Map("/dataTypes/daily-heart-rate-zones/", """
                {
                  "dataPoints": [
                    {
                      "dailyHeartRateZones": {
                        "date": { "year": 2026, "month": 8, "day": 5 },
                        "heartRateZones": [
                          { "heartRateZoneType": "LIGHT",    "minBeatsPerMinute": "78",  "maxBeatsPerMinute": "95"  },
                          { "heartRateZoneType": "MODERATE", "minBeatsPerMinute": "96",  "maxBeatsPerMinute": "112" }
                        ]
                      }
                    }
                  ]
                }
                """);

        var (sut, _) = CreateSut(handler);
        var result = await sut.GetExertionAsync("token", Today);

        Assert.Equal(96, result.ModerateZoneFloorBpm);
    }

    private static string ActivityLevelPoint(string level, string start, string end) => $$"""
        {
          "activityLevel": {
            "activityLevelType": "{{level}}",
            "interval": { "startTime": "{{start}}", "endTime": "{{end}}" }
          }
        }
        """;

    /// <summary>
    /// The reading that cannot be derived from a daily total: touching sedentary intervals are one
    /// unbroken stretch, and a device that emits level-per-minute emits a run of them. A strict
    /// equality test on the boundaries would report the longest stretch as a single interval.
    /// </summary>
    [Fact]
    public async Task GetExertionAsync_JoinsTouchingSedentaryIntervals_IntoOneStretch()
    {
        var handler = new RoutedFakeHttpHandler()
            .Map("/dataTypes/activity-level/", $$"""
                {
                  "dataPoints": [
                    {{ActivityLevelPoint("SEDENTARY", "2026-08-05T13:00:00Z", "2026-08-05T14:00:00Z")}},
                    {{ActivityLevelPoint("SEDENTARY", "2026-08-05T14:00:00Z", "2026-08-05T15:30:00Z")}},
                    {{ActivityLevelPoint("LIGHTLY_ACTIVE", "2026-08-05T15:30:00Z", "2026-08-05T15:45:00Z")}},
                    {{ActivityLevelPoint("SEDENTARY", "2026-08-05T15:45:00Z", "2026-08-05T16:15:00Z")}}
                  ]
                }
                """);

        var (sut, _) = CreateSut(handler);
        var result = await sut.GetExertionAsync("token", new DateOnly(2026, 8, 5));

        // 13:00-15:30 is one stretch of 150 minutes; the 30-minute stretch after the walk is not it.
        Assert.Equal(150, result.LongestSedentaryStretchMinutes);
        Assert.Equal(
            new DateTime(2026, 8, 5, 13, 0, 0, DateTimeKind.Utc), result.LongestSedentaryStretchStartUtc);
    }

    /// <summary>
    /// A gap wider than the join tolerance is a real interruption — the wearer got up — and breaks
    /// the run, which is the entire point of measuring an unbroken stretch.
    /// </summary>
    [Fact]
    public async Task GetExertionAsync_BreaksTheStretch_WhenTheWearerMovedInBetween()
    {
        var handler = new RoutedFakeHttpHandler()
            .Map("/dataTypes/activity-level/", $$"""
                {
                  "dataPoints": [
                    {{ActivityLevelPoint("SEDENTARY", "2026-08-05T09:00:00Z", "2026-08-05T10:00:00Z")}},
                    {{ActivityLevelPoint("SEDENTARY", "2026-08-05T10:20:00Z", "2026-08-05T11:00:00Z")}}
                  ]
                }
                """);

        var (sut, _) = CreateSut(handler);
        var result = await sut.GetExertionAsync("token", new DateOnly(2026, 8, 5));

        Assert.Equal(60, result.LongestSedentaryStretchMinutes);
    }

    [Fact]
    public async Task GetExertionAsync_ReturnsNulls_WhenTheDeviceRecordsNoneOfIt()
    {
        var handler = new RoutedFakeHttpHandler()
            .Map("/dataTypes/daily-heart-rate-zones/", """{ "dataPoints": [] }""")
            .Map("/dataTypes/activity-level/", """{ "dataPoints": [] }""");

        var (sut, _) = CreateSut(handler);
        var result = await sut.GetExertionAsync("token", Today);

        Assert.Null(result.LightZoneMinutes);
        Assert.Null(result.ModerateZoneFloorBpm);
        Assert.Null(result.LongestSedentaryStretchMinutes);
        Assert.Null(result.LongestSedentaryStretchStartUtc);
    }

    /// <summary>
    /// The minute series and the nightly record report the same quantity at two grains, so the
    /// granular read takes RMSSD rather than the record's standard-deviation sibling.
    /// </summary>
    [Fact]
    public async Task GetGranularDayAsync_ReadsHeartRateVariabilitySamples()
    {
        var handler = new RoutedFakeHttpHandler()
            .Map("/dataTypes/heart-rate-variability/", SamplePage(
                "heartRateVariability", "rootMeanSquareOfSuccessiveDifferencesMilliseconds", null,
                "31.5", "28.25"));

        var (sut, _) = CreateSut(handler);
        var day = await ((IDeviceApiClient)sut).GetGranularDayAsync("token", new DateOnly(2026, 8, 5));

        Assert.Equal([31.5f, 28.25f], day.HeartRateVariability.Select(s => s.Value));
    }

    private static string SamplePage(
        string unionMember, string valueField, string? nextPageToken, params string[] values)
    {
        var points = string.Join(",", values.Select(v =>
            $$"""{ "{{unionMember}}": { "sampleTime": { "physicalTime": "2026-08-05T00:00:00Z" }, "{{valueField}}": "{{v}}" } }"""));
        var token = nextPageToken is null ? "" : $$""", "nextPageToken": "{{nextPageToken}}" """;
        return $$"""{ "dataPoints": [ {{points}} ]{{token}} }""";
    }
}
