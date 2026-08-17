using System.Diagnostics;
using System.Reflection;
using CardiTrack.API.Controllers.Dev;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;

namespace CardiTrack.IntegrationTests.Notifications;

/// <summary>
/// notification_engine.md §13 — <see cref="DevPushController"/> sends a real push with no
/// authenticated caller, so the only thing between it and "anyone can notify anyone" is that it
/// does not exist outside a developer's machine. Both locks are checked here, and so is the thing
/// that actually enforces them: that a disabled controller is genuinely removed from MVC's
/// discovered set rather than merely unreachable by convention.
/// </summary>
public class DevPushGatingTests
{
    private const string Key = "yLQb7f0m1Xy0k4V3z8Q9hJ2sN6pR5tW1cE4gU7aD0bM=";

    // ── The predicate ─────────────────────────────────────────────────────────────

    [Fact]
    public void KeyConfiguredAndEnvironmentIsDev_IsEnabled()
    {
        Assert.True(DevPushControllerProvider.IsEnabled(Key, "dev"));
    }

    [Fact]
    public void KeyConfiguredButEnvironmentIsProd_IsDisabled()
    {
        Assert.False(DevPushControllerProvider.IsEnabled(Key, "prod"));
    }

    [Fact]
    public void ProdCheckIgnoresCasing()
    {
        // DeploymentInfo lowercases, but nothing stops ASPNETCORE_ENVIRONMENT arriving as "Prod"
        // — which is exactly what Terraform sets.
        Assert.False(DevPushControllerProvider.IsEnabled(Key, "Prod"));
        Assert.False(DevPushControllerProvider.IsEnabled(Key, "PROD"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoKeyConfigured_IsDisabledEvenOutsideProd(string? key)
    {
        Assert.False(DevPushControllerProvider.IsEnabled(key, "dev"));
    }

    [Fact]
    public void UnsetEnvironmentWithAKey_IsEnabled()
    {
        // A machine that never said which environment it is reports none (DeploymentInfo's rule),
        // and that machine is a laptop. The key check is what stands in front of it.
        Assert.True(DevPushControllerProvider.IsEnabled(Key, null));
    }

    // ── The enforcement ───────────────────────────────────────────────────────────

    [Fact]
    public void Disabled_RemovesTheControllerFromTheDiscoveredSet()
    {
        var feature = DiscoveredControllers();
        Assert.Contains(typeof(DevPushController).GetTypeInfo(), feature.Controllers);

        new DevPushControllerProvider(enabled: false).PopulateFeature([], feature);

        Assert.DoesNotContain(typeof(DevPushController).GetTypeInfo(), feature.Controllers);
    }

    [Fact]
    public void Enabled_LeavesTheControllerInPlace()
    {
        var feature = DiscoveredControllers();

        new DevPushControllerProvider(enabled: true).PopulateFeature([], feature);

        Assert.Contains(typeof(DevPushController).GetTypeInfo(), feature.Controllers);
    }

    [Fact]
    public void Disabled_LeavesEveryOtherControllerAlone()
    {
        var feature = DiscoveredControllers();
        var others = feature.Controllers.Where(c => c != typeof(DevPushController).GetTypeInfo()).ToList();

        new DevPushControllerProvider(enabled: false).PopulateFeature([], feature);

        Assert.Equal(others, feature.Controllers);
        Assert.NotEmpty(feature.Controllers);
    }

    // ── The route table, which is what actually decides 404 ───────────────────────

    [Fact]
    public void Disabled_LeavesNoRouteAtTheDevPushPath()
    {
        Assert.DoesNotContain(DevPushRoute, RouteTemplates(enabled: false));
    }

    [Fact]
    public void Enabled_PublishesTheDevPushRoute()
    {
        Assert.Contains(DevPushRoute, RouteTemplates(enabled: true));
    }

    [Fact]
    public void Disabled_LeavesTheRestOfTheApiRouteTableIntact()
    {
        // The removal must take the one route and nothing else — a provider that emptied the
        // table would pass the test above for the wrong reason.
        var withIt = RouteTemplates(enabled: true);
        var without = RouteTemplates(enabled: false);

        Assert.Equal([DevPushRoute], withIt.Except(without));
        Assert.NotEmpty(without);
    }

    private const string DevPushRoute = "api/v1/dev/push";

    /// <summary>
    /// Every attribute route MVC would publish for the API assembly at the given gate setting —
    /// the same computation routing performs, short of running a host the API's Redis and database
    /// dependencies would otherwise require.
    /// </summary>
    private static HashSet<string> RouteTemplates(bool enabled)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IWebHostEnvironment>(new StubEnvironment());
        services.AddSingleton<DiagnosticSource>(new DiagnosticListener("test"));
        services.AddSingleton(new DiagnosticListener("test"));
        services.AddLogging();
        services
            .AddControllers()
            .ConfigureApplicationPartManager(manager =>
            {
                manager.ApplicationParts.Clear();
                manager.ApplicationParts.Add(new AssemblyPart(typeof(DevPushController).Assembly));
                manager.FeatureProviders.Add(new DevPushControllerProvider(enabled));
            });

        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IActionDescriptorCollectionProvider>()
            .ActionDescriptors.Items
            .Select(a => a.AttributeRouteInfo?.Template)
            .Where(t => t is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase)!;
    }

    private sealed class StubEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = typeof(DevPushController).Assembly.GetName().Name!;
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }

    /// <summary>
    /// MVC's own discovery over the API assembly, so these tests run against the real controller
    /// set rather than a hand-built list that could omit the very type under test.
    /// </summary>
    private static ControllerFeature DiscoveredControllers()
    {
        var manager = new ApplicationPartManager();
        manager.ApplicationParts.Add(new AssemblyPart(typeof(DevPushController).Assembly));
        manager.FeatureProviders.Add(new ControllerFeatureProvider());

        var feature = new ControllerFeature();
        manager.PopulateFeature(feature);
        return feature;
    }
}
