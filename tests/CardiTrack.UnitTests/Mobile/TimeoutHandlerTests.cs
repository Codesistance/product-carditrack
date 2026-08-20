using System.Net;
using CardiTrack.Mobile.Core.Api;

namespace CardiTrack.UnitTests.Mobile;

public class TimeoutHandlerTests
{
    /// <summary>Inner handler that answers only after <paramref name="delay"/>, honouring the
    /// cancellation the timeout handler links in.</summary>
    private sealed class SlowHandler(TimeSpan delay) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(delay, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private static HttpMessageInvoker Invoker(TimeSpan defaultTimeout, TimeSpan innerDelay) =>
        new(new TimeoutHandler(defaultTimeout) { InnerHandler = new SlowHandler(innerDelay) });

    [Fact]
    public async Task Expiry_SurfacesAsTimeout_NotAsCancellation()
    {
        // The distinction NetworkError keys off: "too slow" must not read as "no connection"
        // or as the caller having given up.
        using var invoker = Invoker(TimeSpan.FromMilliseconds(50), innerDelay: TimeSpan.FromMinutes(1));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.test/slow");

        await Assert.ThrowsAsync<TimeoutException>(
            () => invoker.SendAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task PerRequestOption_ExtendsBeyondTheDefault()
    {
        // The member-chat send's case: slower than the client-wide default, still legitimate.
        using var invoker = Invoker(TimeSpan.FromMilliseconds(50), innerDelay: TimeSpan.FromMilliseconds(300));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.test/slow");
        request.Options.Set(TimeoutHandler.TimeoutOption, TimeSpan.FromSeconds(30));

        var response = await invoker.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task CallersOwnCancellation_IsNotRewrittenIntoATimeout()
    {
        using var invoker = Invoker(TimeSpan.FromSeconds(30), innerDelay: TimeSpan.FromMinutes(1));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.test/slow");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => invoker.SendAsync(request, cts.Token));
    }
}
