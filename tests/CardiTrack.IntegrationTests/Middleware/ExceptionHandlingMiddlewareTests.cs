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
/// What a caller is told when a request fails past its controller. Adding a CardiMember past the
/// plan's limit reached here uncaught and came back as "Something went wrong on our end" — a
/// refusal the service had written a reason for, reported as an outage.
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
            Request = { Method = "POST", Path = "/api/v1/onboarding/cardimember" },
            Response = { Body = new MemoryStream() },
        };

        await middleware.InvokeAsync(context);

        context.Response.Body.Position = 0;
        using var body = await JsonDocument.ParseAsync(context.Response.Body);
        return (context.Response.StatusCode, body.RootElement.GetProperty("message").GetString()!);
    }

    [Fact]
    public async Task AFamilyRuleRefusalSaysWhy_WithTheStatusTheControllersUse()
    {
        const string reason = "This family's plan covers 3 CardiMembers, and you're already watching 3.";

        var (status, message) = await RunAsync(
            new FamilyRuleException(FamilyRuleException.CardiMemberLimitReached, reason));

        // 422, as FamiliesController and FamilyJoinController return for the same exception.
        Assert.Equal((int)HttpStatusCode.UnprocessableEntity, status);
        Assert.Equal(reason, message);
    }

    [Fact]
    public async Task AnUnmappedFaultStillHidesItsMessage()
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
