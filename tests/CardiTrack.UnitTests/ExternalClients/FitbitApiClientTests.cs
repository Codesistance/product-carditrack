using System.Net;
using System.Text;
using System.Text.Json;
using CardiTrack.Infrastructure.ExternalClients;
using NSubstitute;

namespace CardiTrack.UnitTests.ExternalClients;

public class FitbitApiClientTests
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

        /// <summary>Snapshot, so a caller cannot enumerate the list while a request is in flight.</summary>
        public IReadOnlyList<HttpRequestMessage> Requests
        {
            get { lock (_recorded) return [.. _requests]; }
        }

        public RoutedFakeHttpHandler Map(string pathContains, string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            _routes.Add((pathContains, body, status));
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
                if (requestBody is not null)
                    _sentBodies.Add((path, requestBody));
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

    private static (IFitbitApiClient Sut, RoutedFakeHttpHandler Handler) CreateSut(RoutedFakeHttpHandler? handler = null)
    {
        handler ??= new RoutedFakeHttpHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://health.googleapis.com") };
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("FitbitClient").Returns(httpClient);
        return (new FitbitApiClient(factory), handler);
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
    /// activity level. Only moderately and very active count, matching Fitbit's classic figure —
    /// including LIGHTLY_ACTIVE would report a number several times what the wearer sees in-app.
    /// </summary>
    [Fact]
    public async Task GetActivitiesAsync_SumsActiveMinutes_ForModeratelyAndVeryActiveOnly()
    {
        var handler = new RoutedFakeHttpHandler()
            .Map("/dataTypes/active-minutes/", Rollup("activeMinutes", """
                {
                  "activeMinutesRollupByActivityLevel": [
                    { "activityLevel": "SEDENTARY",         "activeMinutesSum": "700" },
                    { "activityLevel": "LIGHTLY_ACTIVE",    "activeMinutesSum": "180" },
                    { "activityLevel": "MODERATELY_ACTIVE", "activeMinutesSum": "22"  },
                    { "activityLevel": "VERY_ACTIVE",       "activeMinutesSum": "18"  }
                  ]
                }
                """));

        var (sut, _) = CreateSut(handler);
        var result = await sut.GetActivitiesAsync("token", Today);

        Assert.Equal(40, result.ActiveMinutes);
    }

    [Fact]
    public async Task GetActivitiesAsync_ReturnsZeros_WhenDayHasNoData()
    {
        var (sut, _) = CreateSut(); // every route returns an empty rollup

        var result = await sut.GetActivitiesAsync("token", Today);

        Assert.Equal(0, result.Steps);
        Assert.Equal(0m, result.DistanceKm);
        Assert.Equal(0, result.ActiveMinutes);
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
    public async Task GetActivitiesAsync_ThrowsFitbitApiException_OnNon2xxResponse()
    {
        var handler = new RoutedFakeHttpHandler()
            .Map("/dataTypes/steps/",
                """{ "error": { "status": "UNAUTHENTICATED" } }""", HttpStatusCode.Unauthorized);

        var (sut, _) = CreateSut(handler);

        await Assert.ThrowsAsync<FitbitApiException>(() => sut.GetActivitiesAsync("bad_token", Today));
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
            dailyRestingHeartRate.date >= "2026-08-31" AND dailyRestingHeartRate.date < "2026-09-01"
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

        var ex = await Assert.ThrowsAsync<FitbitApiException>(() => sut.GetHeartRateAsync("token", Today));
        Assert.True(ex.IsMalformedRequest);
    }

    [Fact]
    public async Task GetHeartRateAsync_ThrowsFitbitApiException_OnNon2xxResponse()
    {
        var handler = new RoutedFakeHttpHandler()
            .Map("/dataTypes/heart-rate/", "{}", HttpStatusCode.InternalServerError);

        var (sut, _) = CreateSut(handler);

        await Assert.ThrowsAsync<FitbitApiException>(() => sut.GetHeartRateAsync("token", Today));
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

    [Fact]
    public async Task GetSleepAsync_ReturnsEmptyResult_WhenNoSessions()
    {
        var handler = new RoutedFakeHttpHandler()
            .Map("/dataTypes/sleep/", """{ "dataPoints": [] }""");

        var (sut, _) = CreateSut(handler);
        var result = await sut.GetSleepAsync("token", Today);

        Assert.Equal(0, result.TotalSleepMinutes);
        Assert.Null(result.SleepEfficiency);
        Assert.Null(result.SleepStartTime);
    }

    [Fact]
    public async Task GetSleepAsync_ThrowsFitbitApiException_OnNon2xxResponse()
    {
        var handler = new RoutedFakeHttpHandler()
            .Map("/dataTypes/sleep/", "{}", HttpStatusCode.Unauthorized);

        var (sut, _) = CreateSut(handler);

        await Assert.ThrowsAsync<FitbitApiException>(() => sut.GetSleepAsync("bad_token", Today));
    }
}
