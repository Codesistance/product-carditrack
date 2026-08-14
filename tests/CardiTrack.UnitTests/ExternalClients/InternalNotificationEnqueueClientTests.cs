using System.Net;
using System.Text;
using CardiTrack.Infrastructure.ExternalClients;
using NSubstitute;

namespace CardiTrack.UnitTests.ExternalClients;

/// <summary>
/// The pipeline's enqueue transport posts only the alert id and fails closed on a non-success
/// status — the assessor logs that and keeps the Alert row.
/// </summary>
public class InternalNotificationEnqueueClientTests
{
    [Fact]
    public async Task PostsTheAlertId_ToTheInternalEnqueuePath()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(InternalNotificationEnqueueClient.HttpClientName)
            .Returns(new HttpClient(handler) { BaseAddress = new Uri("https://api.example.test/") });

        var alertId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        await new InternalNotificationEnqueueClient(factory).EnqueueForAlertAsync(alertId);

        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal("/api/v1/internal/notifications/enqueue", handler.Request.RequestUri!.AbsolutePath);
        Assert.Contains("\"alertId\":\"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee\"", handler.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANonSuccessStatus_Throws()
    {
        var handler = new RecordingHandler(HttpStatusCode.ServiceUnavailable);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(InternalNotificationEnqueueClient.HttpClientName)
            .Returns(new HttpClient(handler) { BaseAddress = new Uri("https://api.example.test/") });

        var client = new InternalNotificationEnqueueClient(factory);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.EnqueueForAlertAsync(Guid.NewGuid()));
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;

        public RecordingHandler(HttpStatusCode status) => _status = status;

        public HttpRequestMessage? Request { get; private set; }
        public string Body { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            if (request.Content is not null)
                Body = await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(_status)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
        }
    }
}
