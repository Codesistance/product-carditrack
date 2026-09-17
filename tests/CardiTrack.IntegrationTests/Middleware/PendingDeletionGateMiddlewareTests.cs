using CardiTrack.API.Infrastructure.UserContext;
using CardiTrack.API.Middleware;
using CardiTrack.Domain.Enums;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace CardiTrack.IntegrationTests.Middleware;

/// <summary>
/// Requesting deletion must stop the account working for every path except reading and
/// cancelling that deletion. Signing a client out is not an authorization boundary; this is.
/// </summary>
public class PendingDeletionGateMiddlewareTests
{
    private sealed class FakeUserContext : IUserContext
    {
        public Guid UserId { get; init; } = Guid.NewGuid();
        public string Auth0UserId => "auth0|test";
        public Guid OrganizationId => Guid.Empty;
        public string Email => "caregiver@example.com";
        public UserRole Role => UserRole.Member;
        public bool IsAuthenticated => true;
        public string Locale => "en-GB";
        public bool? EmailVerified => true;
        public DateTime? DeletionRequestedAtUtc { get; init; }
    }

    private static async Task<DefaultHttpContext> RunAsync(
        string method, string path, DateTime? deletionRequestedAtUtc)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();

        var nextCalled = false;
        var middleware = new PendingDeletionGateMiddleware(
            _ =>
            {
                nextCalled = true;
                context.Items["next"] = true;
                return Task.CompletedTask;
            },
            NullLogger<PendingDeletionGateMiddleware>.Instance);

        await middleware.InvokeAsync(context, new FakeUserContext
        {
            DeletionRequestedAtUtc = deletionRequestedAtUtc,
        });

        context.Items["nextCalled"] = nextCalled;
        return context;
    }

    [Fact]
    public async Task AnAccountNotAwaitingDeletion_IsPassedThrough()
    {
        var context = await RunAsync("GET", "/api/v1/cardimembers", deletionRequestedAtUtc: null);

        Assert.True((bool)context.Items["nextCalled"]!);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task APendingDeletion_RefusesHealthReads()
    {
        var context = await RunAsync(
            "GET", "/api/v1/cardimembers", DateTime.UtcNow.AddDays(-1));

        Assert.False((bool)context.Items["nextCalled"]!);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        Assert.Contains("scheduled for deletion", body, StringComparison.Ordinal);
        Assert.Contains("monitoring has stopped", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("DELETE")]
    [InlineData("POST")]
    public async Task TheDeletionEndpoint_StaysReachable(string method)
    {
        var context = await RunAsync(
            method, "/api/v1/users/me/deletion", DateTime.UtcNow.AddDays(-1));

        Assert.True((bool)context.Items["nextCalled"]!);
        Assert.NotEqual(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Fact]
    public async Task TheDeletionPathIsMatchedCaseInsensitively()
    {
        var context = await RunAsync(
            "GET", "/API/V1/USERS/ME/DELETION", DateTime.UtcNow.AddDays(-1));

        Assert.True((bool)context.Items["nextCalled"]!);
    }
}
