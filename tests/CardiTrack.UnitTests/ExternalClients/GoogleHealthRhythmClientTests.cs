using System.Net;
using CardiTrack.Infrastructure.ExternalClients;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace CardiTrack.UnitTests.ExternalClients;

/// <summary>
/// Wire shapes for the two rhythm data types. These matter more than most client tests because
/// the failure mode is silent: a field name that stops matching does not throw, it produces zero
/// readings and zero notifications, which reads on every screen as a wearer whose heart is
/// behaving. Field names here are taken from the v4 discovery document (revision 20260819).
/// </summary>
public class GoogleHealthRhythmClientTests
{
    private const string EcgPath = "/dataTypes/electrocardiogram/";
    private const string IrnPath = "/dataTypes/irregular-rhythm-notification/";

    private static (IGoogleHealthApiClient Sut, FakeHandler Handler) CreateSut(FakeHandler handler)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://health.googleapis.com") };
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("GoogleHealthClient").Returns(httpClient);

        return (
            new GoogleHealthApiClient(
                factory,
                Substitute.For<ILogger<GoogleHealthApiClient>>(),
                TimeSpan.Zero,
                null),
            handler);
    }

    /// <summary>
    /// Minimal router: successive bodies per path substring, and an empty page for anything
    /// unmapped so one data type's test does not have to stub the other.
    /// </summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, Queue<(string Body, HttpStatusCode Status)>> _routes = new(StringComparer.Ordinal);
        public List<string> Urls { get; } = [];

        public FakeHandler Map(string pathContains, string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            if (!_routes.TryGetValue(pathContains, out var queue))
                _routes[pathContains] = queue = new Queue<(string, HttpStatusCode)>();
            queue.Enqueue((body, status));
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            lock (Urls) Urls.Add(url);

            foreach (var (path, queue) in _routes)
            {
                if (!url.Contains(path, StringComparison.Ordinal) || queue.Count == 0)
                    continue;

                var (body, status) = queue.Dequeue();
                return Task.FromResult(new HttpResponseMessage(status)
                {
                    Content = new StringContent(body),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"dataPoints":[]}"""),
            });
        }
    }

    private static string Ecg(string classification, DateOnly civilDate, string startTime) =>
        "{\"dataPoints\":[{\"electrocardiogram\":{"
        + "\"resultClassification\":\"" + classification + "\","
        + "\"interval\":{\"startTime\":\"" + startTime + "\",\"civilStartTime\":{\"date\":{"
        + "\"year\":" + civilDate.Year + ",\"month\":" + civilDate.Month + ",\"day\":" + civilDate.Day
        + "}}}}}]}";

    // ── ECG ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Ecg_counts_only_the_atrial_fibrillation_classification()
    {
        var handler = new FakeHandler().Map(EcgPath, """
            {"dataPoints":[
              {"electrocardiogram":{"resultClassification":"ATRIAL_FIBRILLATION","interval":{"startTime":"2026-09-22T10:00:00Z","civilStartTime":{"date":{"year":2026,"month":9,"day":22}}}}},
              {"electrocardiogram":{"resultClassification":"NORMAL_SINUS_RHYTHM","interval":{"startTime":"2026-09-22T09:00:00Z","civilStartTime":{"date":{"year":2026,"month":9,"day":22}}}}},
              {"electrocardiogram":{"resultClassification":"INCONCLUSIVE_HIGH_HEART_RATE","interval":{"startTime":"2026-09-22T08:00:00Z","civilStartTime":{"date":{"year":2026,"month":9,"day":22}}}}}
            ]}
            """);

        var (sut, _) = CreateSut(handler);
        var day = await sut.GetRhythmDayAsync("token", new DateOnly(2026, 9, 22));

        // All three were taken; only the first is a finding. The inconclusive member means the
        // device declined to judge and must never be counted as a positive.
        Assert.Equal(3, day.EcgReadings);
        Assert.Equal(1, day.EcgAtrialFibrillationReadings);
    }

    [Fact]
    public async Task Ecg_never_asks_for_the_waveform()
    {
        var handler = new FakeHandler().Map(EcgPath, """{"dataPoints":[]}""");
        var (sut, h) = CreateSut(handler);

        await sut.GetRhythmDayAsync("token", new DateOnly(2026, 9, 22));

        var ecgUrl = h.Urls.Single(u => u.Contains(EcgPath, StringComparison.Ordinal));
        Assert.Contains("fields=", ecgUrl, StringComparison.Ordinal);
        Assert.Contains("resultClassification", Uri.UnescapeDataString(ecgUrl), StringComparison.Ordinal);
        Assert.DoesNotContain("waveformSamples", Uri.UnescapeDataString(ecgUrl), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ecg_buckets_by_the_wearers_civil_day_not_the_utc_instant()
    {
        // 01:30Z on the 23rd is still the 22nd for a wearer west of Greenwich, and the reading
        // carries that civil date. Bucketing on the instant would move it to the wrong day.
        var handler = new FakeHandler().Map(EcgPath,
            Ecg("ATRIAL_FIBRILLATION", new DateOnly(2026, 9, 22), "2026-09-23T01:30:00Z"));

        var (sut, _) = CreateSut(handler);
        var day = await sut.GetRhythmDayAsync("token", new DateOnly(2026, 9, 22));

        Assert.Equal(1, day.EcgAtrialFibrillationReadings);
    }

    [Fact]
    public async Task Ecg_stops_walking_at_the_first_reading_older_than_the_requested_day()
    {
        // The response is ordered descending and the filter has no upper bound, so the walk must
        // terminate on age rather than paging back through the wearer's whole history.
        var handler = new FakeHandler().Map(EcgPath, """
            {"nextPageToken":"p2","dataPoints":[
              {"electrocardiogram":{"resultClassification":"ATRIAL_FIBRILLATION","interval":{"startTime":"2026-09-22T10:00:00Z","civilStartTime":{"date":{"year":2026,"month":9,"day":22}}}}},
              {"electrocardiogram":{"resultClassification":"ATRIAL_FIBRILLATION","interval":{"startTime":"2026-09-21T10:00:00Z","civilStartTime":{"date":{"year":2026,"month":9,"day":21}}}}}
            ]}
            """);

        var (sut, h) = CreateSut(handler);
        var day = await sut.GetRhythmDayAsync("token", new DateOnly(2026, 9, 22));

        Assert.Equal(1, day.EcgAtrialFibrillationReadings);
        Assert.Single(h.Urls, u => u.Contains(EcgPath, StringComparison.Ordinal));
    }

    // ── IRN ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Irn_counts_notifications_and_recovers_rr_from_beats_per_minute()
    {
        var handler = new FakeHandler().Map(IrnPath, """
            {"dataPoints":[{"irregularRhythmNotification":{
                "interval":{"startTime":"2026-09-22T03:00:00Z"},
                "alertWindows":[{
                    "startTime":"2026-09-22T03:00:00Z",
                    "endTime":"2026-09-22T03:05:00Z",
                    "positive":true,
                    "heartBeats":[
                        {"physicalTime":"2026-09-22T03:00:00Z","beatsPerMinute":75},
                        {"physicalTime":"2026-09-22T03:00:01Z","beatsPerMinute":60}
                    ]
                }]
            }}]}
            """);

        var (sut, _) = CreateSut(handler);
        var day = await sut.GetRhythmDayAsync("token", new DateOnly(2026, 9, 22));

        // One notification, not one per alert window: a notification is one thing the wearer was
        // told about.
        Assert.Equal(1, day.IrregularRhythmNotifications);

        var window = Assert.Single(day.AnalysisWindows);
        Assert.True(window.Positive);

        // 60000/75 = 800, 60000/60 = 1000, and the second beat sits a second into the window.
        Assert.Equal([(0, 800), (1000, 1000)], window.Beats);
    }

    [Fact]
    public async Task Irn_keeps_a_window_the_provider_served_without_beat_detail()
    {
        // heartBeats is optional in the schema. A window without it is a window without detail,
        // not a missing episode — dropping it would let the episode list disagree with the count.
        var handler = new FakeHandler().Map(IrnPath, """
            {"dataPoints":[{"irregularRhythmNotification":{
                "interval":{"startTime":"2026-09-22T03:00:00Z"},
                "alertWindows":[{"startTime":"2026-09-22T03:00:00Z","endTime":"2026-09-22T03:05:00Z","positive":true}]
            }}]}
            """);

        var (sut, _) = CreateSut(handler);
        var day = await sut.GetRhythmDayAsync("token", new DateOnly(2026, 9, 22));

        Assert.Equal(1, day.IrregularRhythmNotifications);
        Assert.Empty(Assert.Single(day.AnalysisWindows).Beats);
    }

    [Fact]
    public async Task Irn_drops_a_beat_reporting_a_nonsense_rate()
    {
        // Zero would invert to a division by zero and a negative to a negative interval.
        var handler = new FakeHandler().Map(IrnPath, """
            {"dataPoints":[{"irregularRhythmNotification":{
                "interval":{"startTime":"2026-09-22T03:00:00Z"},
                "alertWindows":[{
                    "startTime":"2026-09-22T03:00:00Z","endTime":"2026-09-22T03:05:00Z","positive":true,
                    "heartBeats":[
                        {"physicalTime":"2026-09-22T03:00:00Z","beatsPerMinute":0},
                        {"physicalTime":"2026-09-22T03:00:01Z","beatsPerMinute":60}
                    ]
                }]
            }}]}
            """);

        var (sut, _) = CreateSut(handler);
        var day = await sut.GetRhythmDayAsync("token", new DateOnly(2026, 9, 22));

        Assert.Equal([(1000, 1000)], Assert.Single(day.AnalysisWindows).Beats);
    }

    [Fact]
    public async Task Irn_follows_pagination()
    {
        var handler = new FakeHandler()
            .Map(IrnPath, """
                {"nextPageToken":"p2","dataPoints":[{"irregularRhythmNotification":{
                    "interval":{"startTime":"2026-09-22T03:00:00Z"},"alertWindows":[]}}]}
                """)
            .Map(IrnPath, """
                {"dataPoints":[{"irregularRhythmNotification":{
                    "interval":{"startTime":"2026-09-22T04:00:00Z"},"alertWindows":[]}}]}
                """);

        var (sut, _) = CreateSut(handler);
        var day = await sut.GetRhythmDayAsync("token", new DateOnly(2026, 9, 22));

        Assert.Equal(2, day.IrregularRhythmNotifications);
    }

    // ── Refusals ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_forbidden_read_yields_null_counts_rather_than_zero()
    {
        // The distinction is the whole point: null is "we could not look", zero is "we looked and
        // the device raised nothing". Only the second is reassuring, and a 403 is the expected
        // steady state for a connection that never granted the rhythm scopes.
        var handler = new FakeHandler()
            .Map(EcgPath, "{}", HttpStatusCode.Forbidden)
            .Map(IrnPath, "{}", HttpStatusCode.Forbidden);

        var (sut, _) = CreateSut(handler);
        var day = await sut.GetRhythmDayAsync("token", new DateOnly(2026, 9, 22));

        Assert.Null(day.EcgReadings);
        Assert.Null(day.EcgAtrialFibrillationReadings);
        Assert.Null(day.IrregularRhythmNotifications);
        Assert.False(day.HasAnyData);
    }

    [Fact]
    public async Task One_refused_read_does_not_cost_the_other()
    {
        var handler = new FakeHandler()
            .Map(EcgPath, "{}", HttpStatusCode.Forbidden)
            .Map(IrnPath, """{"dataPoints":[]}""");

        var (sut, _) = CreateSut(handler);
        var day = await sut.GetRhythmDayAsync("token", new DateOnly(2026, 9, 22));

        Assert.Null(day.EcgReadings);
        Assert.Equal(0, day.IrregularRhythmNotifications);
    }

    [Fact]
    public async Task IrnProfile_reads_the_boolean_status_fields()
    {
        // Both are documented as `"type": "boolean"` in the v4 discovery document, not as status
        // enum strings.
        var handler = new FakeHandler().Map(
            "/irnProfile", """{"onboardingStatus":true,"enrollmentStatus":false}""");

        var (sut, _) = CreateSut(handler);
        var (onboarded, enrolled) = await sut.GetIrnProfileAsync("token");

        Assert.True(onboarded);
        Assert.False(enrolled);
    }

    [Fact]
    public async Task IrnProfile_yields_nulls_when_the_scope_was_never_granted()
    {
        var handler = new FakeHandler().Map("/irnProfile", "{}", HttpStatusCode.Forbidden);

        var (sut, _) = CreateSut(handler);
        var (onboarded, enrolled) = await sut.GetIrnProfileAsync("token");

        Assert.Null(onboarded);
        Assert.Null(enrolled);
    }
}
