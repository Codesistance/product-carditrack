using System.Net;
using System.Text;
using CardiTrack.Infrastructure.ExternalClients.Environmental;
using CardiTrack.Infrastructure.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace CardiTrack.UnitTests.ExternalClients;

/// <summary>
/// What the enrichment pass gets back from Google's Weather and Air Quality APIs — and, as much as
/// anything, what it still gets back when one of them does not answer.
/// </summary>
public class GoogleEnvironmentalClientTests
{
    /// <summary>
    /// Routes by host, because the client calls two independent APIs in parallel and the point of
    /// several of these tests is that one can fail without taking the other with it.
    /// </summary>
    private sealed class RoutingHandler(string? weatherBody, string? airQualityBody) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var isWeather = request.RequestUri!.Host.Contains("weather", StringComparison.OrdinalIgnoreCase);
            var body = isWeather ? weatherBody : airQualityBody;

            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                });
        }
    }

    private const string AirQualityBody =
        """{"indexes":[{"code":"uaqi","aqi":42,"category":"Good air quality"}]}""";

    private static GoogleEnvironmentalClient Client(HttpMessageHandler handler)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler, disposeHandler: false));

        return new GoogleEnvironmentalClient(
            factory,
            new EnvironmentalContextSettings { ApiKey = "secret-key" },
            "EnvironmentalClient",
            NullLogger<GoogleEnvironmentalClient>.Instance);
    }

    private static Task<CardiTrack.Application.Interfaces.Clients.EnvironmentalContext> GetAsync(
        string? weatherBody, string? airQualityBody = AirQualityBody) =>
        Client(new RoutingHandler(weatherBody, airQualityBody))
            .GetContextAsync(51.5, -0.12, DateTime.UtcNow);

    /// <summary>
    /// Condition and humidity ride along on the current-conditions response the client was already
    /// making for temperature — the fields were simply not being read.
    /// </summary>
    [Fact]
    public async Task ReadsTemperatureConditionAndHumidity_FromOneLookup()
    {
        var context = await GetAsync("""
            {"temperature":{"degrees":18.4,"unit":"CELSIUS"},
             "weatherCondition":{"type":"LIGHT_RAIN","description":{"text":"Light rain"}},
             "relativeHumidity":78}
            """);

        Assert.Equal(18.4, context.TemperatureCelsius);
        Assert.Equal("Light rain", context.WeatherCondition);
        Assert.Equal(78, context.RelativeHumidityPercent);
        Assert.Equal(42, context.AirQualityIndex);
        Assert.Equal("Good air quality", context.AirQualityCategory);
    }

    /// <summary>
    /// The description is what belongs in a sentence a caregiver reads; the enum-style type is only
    /// better than nothing.
    /// </summary>
    [Fact]
    public async Task FallsBackToTheConditionType_WhenNoDescriptionCameBack()
    {
        var context = await GetAsync("""
            {"temperature":{"degrees":12.0},"weatherCondition":{"type":"CLOUDY"}}
            """);

        Assert.Equal("CLOUDY", context.WeatherCondition);
    }

    [Fact]
    public async Task ConvertsFahrenheit_SoTheStoredUnitIsAlwaysCelsius()
    {
        var context = await GetAsync("""{"temperature":{"degrees":68.0,"unit":"FAHRENHEIT"}}""");

        Assert.Equal(20.0, context.TemperatureCelsius!.Value, 3);
    }

    /// <summary>Each field is independently optional — a sparse response is partial, not failed.</summary>
    [Fact]
    public async Task ToleratesAResponseCarryingNothingItLooksFor()
    {
        var context = await GetAsync("{}");

        Assert.Null(context.TemperatureCelsius);
        Assert.Null(context.WeatherCondition);
        Assert.Null(context.RelativeHumidityPercent);
        Assert.Equal(42, context.AirQualityIndex);
    }

    [Fact]
    public async Task AFailingWeatherLookup_DoesNotCostTheAirQualityReading()
    {
        var context = await GetAsync(weatherBody: null);

        Assert.Null(context.TemperatureCelsius);
        Assert.Null(context.WeatherCondition);
        Assert.Equal(42, context.AirQualityIndex);
    }

    [Fact]
    public async Task AFailingAirQualityLookup_DoesNotCostTheWeather()
    {
        var context = await GetAsync(
            """{"temperature":{"degrees":15.0},"relativeHumidity":55}""", airQualityBody: null);

        Assert.Equal(15.0, context.TemperatureCelsius);
        Assert.Equal(55, context.RelativeHumidityPercent);
        Assert.Null(context.AirQualityIndex);
    }

    /// <summary>
    /// A misrouted request can answer with an HTML error page. Uncaught, the JSON exception would
    /// fault this task and take the sibling call down with it through Task.WhenAll.
    /// </summary>
    [Fact]
    public async Task AnUnparseableBody_IsTreatedAsNoReading()
    {
        var context = await GetAsync("<html>gateway error</html>");

        Assert.Null(context.TemperatureCelsius);
        Assert.Equal(42, context.AirQualityIndex);
    }

    /// <summary>
    /// A timeout and a shutdown both arrive as <see cref="TaskCanceledException"/>, and they are
    /// not the same thing. The timeout is a lookup that failed and is swallowed like any other;
    /// a signalled token is the host stopping the job, and swallowing that would turn a prompt
    /// shutdown into a full pass over every remaining member, each logging a warning on the way out.
    /// </summary>
    [Fact]
    public async Task ATimeout_IsSwallowedLikeAnyOtherFailedLookup()
    {
        var context = await Client(new CancellingHandler(new CancellationTokenSource().Token))
            .GetContextAsync(51.5, -0.12, DateTime.UtcNow);

        Assert.Null(context.TemperatureCelsius);
        Assert.Null(context.AirQualityIndex);
    }

    [Fact]
    public async Task ASignalledToken_StopsTheLookupRatherThanBeingLoggedAndIgnored()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Client(new CancellingHandler(cts.Token))
                .GetContextAsync(51.5, -0.12, DateTime.UtcNow, cts.Token));
    }

    /// <summary>Throws the exception an HttpClient raises for both a timeout and a cancellation.</summary>
    private sealed class CancellingHandler(CancellationToken token) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new TaskCanceledException("timed out", null, token);
    }
}
