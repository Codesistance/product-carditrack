using System.Net;
using System.Text;
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

        public List<HttpRequestMessage> Requests { get; } = [];

        public RoutedFakeHttpHandler Map(string pathContains, string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            _routes.Add((pathContains, body, status));
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var path = request.RequestUri!.AbsolutePath;
            var route = _routes.FirstOrDefault(r => path.Contains(r.PathContains, StringComparison.Ordinal));
            var body = route == default ? """{ "rollupDataPoints": [] }""" : route.Body;
            var status = route == default ? HttpStatusCode.OK : route.Status;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
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

    [Fact]
    public async Task GetActivitiesAsync_ReturnsSteps_FromDailyRollup()
    {
        var handler = new RoutedFakeHttpHandler()
            .Map("/dataTypes/steps/", Rollup("steps", """{ "count": 9423 }"""));

        var (sut, _) = CreateSut(handler);
        var result = await sut.GetActivitiesAsync("token", Today);

        Assert.Equal(9423, result.Steps);
    }

    [Fact]
    public async Task GetActivitiesAsync_ConvertsDistanceMetersToKm()
    {
        var handler = new RoutedFakeHttpHandler()
            .Map("/dataTypes/distance/", Rollup("distance", """{ "meters_sum": 6300 }"""));

        var (sut, _) = CreateSut(handler);
        var result = await sut.GetActivitiesAsync("token", Today);

        Assert.Equal(6.3m, result.DistanceKm);
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
    public async Task GetActivitiesAsync_ThrowsFitbitApiException_OnNon2xxResponse()
    {
        var handler = new RoutedFakeHttpHandler()
            .Map("/dataTypes/steps/",
                """{ "error": { "status": "UNAUTHENTICATED" } }""", HttpStatusCode.Unauthorized);

        var (sut, _) = CreateSut(handler);

        await Assert.ThrowsAsync<FitbitApiException>(() => sut.GetActivitiesAsync("bad_token", Today));
    }

    // ── Heart Rate ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetHeartRateAsync_ReturnsMinMaxAvg_FromDailyRollup()
    {
        var handler = new RoutedFakeHttpHandler()
            .Map("/dataTypes/heart-rate/", Rollup("heartRate",
                """{ "beatsPerMinute_min": 52, "beatsPerMinute_max": 141, "beatsPerMinute_avg": 71 }"""))
            .Map("/dataTypes/resting-heart-rate/", Rollup("restingHeartRate",
                """{ "beatsPerMinute_avg": 63 }"""));

        var (sut, _) = CreateSut(handler);
        var result = await sut.GetHeartRateAsync("token", Today);

        Assert.Equal(52, result.MinHeartRate);
        Assert.Equal(141, result.MaxHeartRate);
        Assert.Equal(71, result.AvgHeartRate);
        Assert.Equal(63, result.RestingHeartRate);
    }

    [Fact]
    public async Task GetHeartRateAsync_ToleratesMissingRestingHeartRateDataType()
    {
        var handler = new RoutedFakeHttpHandler()
            .Map("/dataTypes/heart-rate/", Rollup("heartRate",
                """{ "beatsPerMinute_min": 52, "beatsPerMinute_max": 141, "beatsPerMinute_avg": 71 }"""))
            .Map("/dataTypes/resting-heart-rate/",
                """{ "error": { "status": "NOT_FOUND" } }""", HttpStatusCode.NotFound);

        var (sut, _) = CreateSut(handler);
        var result = await sut.GetHeartRateAsync("token", Today);

        Assert.Null(result.RestingHeartRate);
        Assert.Equal(71, result.AvgHeartRate);
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

    private const string SleepSessionJson = """
        {
          "dataPoints": [
            {
              "sleep": {
                "interval": {
                  "startTime": "2026-08-04T22:30:00Z",
                  "endTime":   "2026-08-05T06:30:00Z"
                },
                "efficiency": 91,
                "stageSummary": {
                  "deepSleepMinutes": 85,
                  "lightSleepMinutes": 220,
                  "remSleepMinutes": 90,
                  "awakeMinutes": 25
                }
              }
            }
          ]
        }
        """;

    [Fact]
    public async Task GetSleepAsync_SumsStageMinutes_WhenTotalAbsent()
    {
        var handler = new RoutedFakeHttpHandler()
            .Map("/dataTypes/sleep/", SleepSessionJson);

        var (sut, _) = CreateSut(handler);
        var result = await sut.GetSleepAsync("token", Today);

        Assert.Equal(395, result.TotalSleepMinutes); // 85 deep + 220 light + 90 rem
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
        Assert.Equal(91, result.SleepEfficiency);
        Assert.NotNull(result.SleepStartTime);
        Assert.NotNull(result.SleepEndTime);
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
