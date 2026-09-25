using System.Net;
using System.Text.Json;
using CardiTrack.API.Middleware;
using CardiTrack.Application.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace CardiTrack.IntegrationTests.Middleware;

/// <summary>
/// What a caller is told when a request fails past its controller.
/// </summary>
public class ExceptionHandlingMiddlewareTests
{
    private static async Task<(int Status, string Message)> RunAsync(Exception thrown)
    {
        var middleware = new ExceptionHandlingMiddleware(
            _ => throw thrown,
            NullLogger<ExceptionHandlingMiddleware>.Instance,
            new ProductionEnvironment());

        var context = new DefaultHttpContext
        {
            Request = { Method = "POST", Path = "/api/v1/caregiver-invites/token/accept" },
            Response = { Body = new MemoryStream() },
        };

        await middleware.InvokeAsync(context);

        context.Response.Body.Position = 0;
        using var body = await JsonDocument.ParseAsync(context.Response.Body);
        return (context.Response.StatusCode, body.RootElement.GetProperty("message").GetString()!);
    }

    /// <summary>
    /// A family rule's message is written for somebody already inside the family, so it is echoed
    /// only by the controllers that know their caller is. Left to fall through here, where the
    /// caller may be an invitee not yet inside — the plan's limit and the family's headcount are
    /// not theirs to read. Mapping it globally was tried and would have told them.
    /// </summary>
    [Fact]
    public async Task AFamilyRuleThatNoControllerCaught_DoesNotLeakTheFamilysPlan()
    {
        var (status, message) = await RunAsync(new FamilyRuleException(
            FamilyRuleException.MemberLimitReached,
            "This family's plan covers 5 people, and 5 are already in it."));

        Assert.Equal((int)HttpStatusCode.InternalServerError, status);
        Assert.DoesNotContain("plan covers", message);
    }

    [Fact]
    public async Task AnUnmappedFaultHidesItsMessage()
    {
        var (status, message) = await RunAsync(new InvalidOperationException("connection string leaked here"));

        Assert.Equal((int)HttpStatusCode.InternalServerError, status);
        Assert.DoesNotContain("connection string", message);
    }

    /// <summary>Production, so the handler never adds its development-only exception detail.</summary>
    private sealed class ProductionEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "CardiTrack.API.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
